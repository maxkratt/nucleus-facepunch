using System;
using Nucleus.Managers.Transports;
using Steamworks;
using UnityEditor;
using UnityEngine;

namespace Nucleus.Integrations.Facepunch.Editor
{
    /// <summary>
    /// Inspector presentation for <see cref="FacepunchTransport"/>. A status box heads it, tinted by state. Settings follow under Connection
    /// and Session headers, with Steam's send settings in a collapsed Advanced section. While playing the settings are greyed out, since they
    /// were read at start, and a running server shows how clients reach it, with a button that copies its SteamID or fake address.
    /// </summary>
    [CustomEditor(typeof(FacepunchTransport))]
    [CanEditMultipleObjects]
    public class FacepunchTransportEditor : UnityEditor.Editor
    {
        /// <summary>
        /// The status line's text, rebuilt when play mode changes and, while playing, at each runtime refresh.
        /// </summary>
        private string _statusText = string.Empty;
        /// <summary>
        /// The colour of the status line's dot.
        /// </summary>
        private Color _statusColor;
        /// <summary>
        /// The editor time, in seconds, at which the runtime readout is next refreshed.
        /// </summary>
        private double _nextRuntimeRefreshTime;
        /// <summary>
        /// The label of the row naming how clients reach this peer's server, or empty while no server runs.
        /// </summary>
        private string _serverRowLabel = string.Empty;
        /// <summary>
        /// The value of that row: the server's SteamID, fake address or port.
        /// </summary>
        private string _serverRowText = string.Empty;
        /// <summary>
        /// What the row's Copy button copies, or <see langword="null"/> when there is nothing a client could paste.
        /// </summary>
        private string _serverRowCopyText;
        /// <summary>
        /// The warning drawn under the status box while playing when Steam is not running, or empty when none is due.
        /// </summary>
        private string _steamWarningText = string.Empty;
        /// <summary>
        /// The version of Facepunch.Steamworks shipped in this package, shown in the status box.
        /// </summary>
        private const string FacepunchVersionText = "Facepunch.Steamworks 2.5.2";
        /// <summary>
        /// The send setting that leaves Steam's value alone.
        /// </summary>
        private const uint UnsetSendSetting = FacepunchSockets.UnsetSendSetting;
        /// <summary>
        /// Steam's default send rate, in bytes per second.
        /// </summary>
        private const uint SteamDefaultSendRateBytes = 256U * 1024U;
        /// <summary>
        /// Steam's default send buffer, in bytes.
        /// </summary>
        private const uint SteamDefaultSendBufferBytes = 512U * 1024U;
        /// <summary>
        /// Steam's default connecting and idle timeout, in seconds.
        /// </summary>
        private const int SteamDefaultTimeoutSeconds = 10;
        /// <summary>
        /// The bytes in one kibibyte, the unit the send settings are entered in.
        /// </summary>
        private const uint KibibyteBytes = 1024U;
        /// <summary>
        /// The longest message a reliable packet is sent in, in bytes.
        /// </summary>
        private const uint SteamMessagePieceBytes = 512U * 1024U;
        /// <summary>
        /// The session key prefix that records whether each section is expanded, followed by the section's title.
        /// </summary>
        private const string SectionExpandedSessionKeyPrefix = "Nucleus.Facepunch.TransportSection.";
        /// <summary>
        /// The gap between a unit and the right edge of its field.
        /// </summary>
        private const float UnitPadding = 4f;
        /// <summary>
        /// The width of the foldout arrow, where a section's title starts.
        /// </summary>
        private const float FoldoutArrowWidth = 15f;
        /// <summary>
        /// The space above each group's header.
        /// </summary>
        private const float GroupSpacing = 6f;
        /// <summary>
        /// The diameter of the status line's dot.
        /// </summary>
        private const float StatusDotSize = 8f;
        /// <summary>
        /// The width of the server row's Copy button.
        /// </summary>
        private const float CopyButtonWidth = 48f;
        /// <summary>
        /// The height of the status box.
        /// </summary>
        private const float StatusBoxHeight = 26f;
        /// <summary>
        /// The height of the version pill inside the status box.
        /// </summary>
        private const float StatusPillHeight = 16f;
        /// <summary>
        /// The corner radius of the status box.
        /// </summary>
        private const float StatusCornerRadius = 4f;
        /// <summary>
        /// The space inside the status box around its dot, text and pill.
        /// </summary>
        private const float StatusPadding = 8f;
        /// <summary>
        /// How strongly the state's colour tints the status box.
        /// </summary>
        private const float StatusFillAlpha = 0.12f;
        /// <summary>
        /// How strongly the state's colour draws the status box's border.
        /// </summary>
        private const float StatusBorderAlpha = 0.35f;
        /// <summary>
        /// How often, in seconds, the status box and the server row are refreshed while playing.
        /// </summary>
        private const double RuntimeRefreshIntervalSeconds = 0.25d;
        /// <summary>
        /// The fill of the version pill: a dark wash that reads on any tint.
        /// </summary>
        private static readonly Color StatusPillColor = new(0f, 0f, 0f, 0.25f);
        /// <summary>
        /// The status dot while the transport is running.
        /// </summary>
        private static readonly Color RunningColor = new(0.35f, 0.75f, 0.4f);
        /// <summary>
        /// The status dot while the transport is idle.
        /// </summary>
        private static readonly Color IdleColor = new(0.55f, 0.55f, 0.55f);

        /// <inheritdoc/>
        public override void OnInspectorGUI()
        {
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.PropertyField(serializedObject.FindProperty("m_Script"));

            DrawStatusBox();
            DrawServerRow();

            if (_steamWarningText.Length > 0)
                EditorGUILayout.HelpBox(_steamWarningText, MessageType.Warning);

            serializedObject.Update();

            if (Application.isPlaying)
                EditorGUILayout.HelpBox("Settings were read when the transport started, and Unity discards changes made while playing. Edit them in edit mode.", MessageType.None);

            using (new EditorGUI.DisabledScope(Application.isPlaying))
            {
                DrawConnectionGroup();
                DrawSessionGroup();
                DrawAdvancedSection();
            }

            serializedObject.ApplyModifiedProperties();
        }

        /// <summary>
        /// Builds the status line and starts the runtime refresh.
        /// </summary>
        private void OnEnable()
        {
            RefreshStatus();

            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.update += OnRuntimeRefreshDue;
        }

        /// <summary>
        /// Stops the runtime refresh.
        /// </summary>
        private void OnDisable()
        {
            EditorApplication.update -= OnRuntimeRefreshDue;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        }

        /// <summary>
        /// Rebuilds the status box and the server row as play mode starts or ends.
        /// </summary>
        /// <param name="playModeStateChange">The change.</param>
        private void OnPlayModeStateChanged(PlayModeStateChange playModeStateChange)
        {
            RefreshStatus();
            RefreshRuntime();
            Repaint();
        }

        /// <summary>
        /// Refreshes the status box and the server row a few times a second while playing.
        /// </summary>
        /// <remarks>The role and the fake address change with no event this inspector can hear, so they are read on a clock, as any live readout is.</remarks>
        private void OnRuntimeRefreshDue()
        {
            if (!Application.isPlaying || EditorApplication.timeSinceStartup < _nextRuntimeRefreshTime)
                return;

            _nextRuntimeRefreshTime = EditorApplication.timeSinceStartup + RuntimeRefreshIntervalSeconds;

            RefreshStatus();
            RefreshRuntime();
            Repaint();
        }

        /// <summary>
        /// Draws the status box: a rounded box tinted by state, holding a dot, the state, and the Facepunch version in a pill at the right.
        /// </summary>
        private void DrawStatusBox()
        {
            EditorGUILayout.Space(2f);

            Rect boxRect = EditorGUILayout.GetControlRect(false, StatusBoxHeight);

            if (Event.current.type is EventType.Repaint)
            {
                Color fillColor = new(_statusColor.r, _statusColor.g, _statusColor.b, StatusFillAlpha);
                Color borderColor = new(_statusColor.r, _statusColor.g, _statusColor.b, StatusBorderAlpha);

                GUI.DrawTexture(boxRect, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, fillColor, 0f, StatusCornerRadius);
                GUI.DrawTexture(boxRect, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, borderColor, 1f, StatusCornerRadius);
            }

            Rect dotRect = new(boxRect.x + StatusPadding, boxRect.y + (boxRect.height - StatusDotSize) / 2f, StatusDotSize, StatusDotSize);

            GUI.DrawTexture(dotRect, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, _statusColor, 0f, StatusDotSize / 2f);

            GUIContent versionGuiContent = new(FacepunchVersionText);
            Vector2 versionSize = EditorStyles.miniLabel.CalcSize(versionGuiContent);
            float pillWidth = versionSize.x + StatusPadding * 2f;
            Rect pillRect = new(boxRect.xMax - pillWidth - StatusPadding / 2f, boxRect.y + (boxRect.height - StatusPillHeight) / 2f, pillWidth, StatusPillHeight);

            GUI.DrawTexture(pillRect, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, StatusPillColor, 0f, StatusPillHeight / 2f);
            GUI.Label(new Rect(pillRect.x + StatusPadding, pillRect.y, versionSize.x, pillRect.height), versionGuiContent, EditorStyles.miniLabel);

            EditorGUI.LabelField(new Rect(dotRect.xMax + StatusPadding, boxRect.y, boxRect.width - (dotRect.xMax - boxRect.x) - StatusPadding * 2f - pillWidth, boxRect.height), _statusText);
            EditorGUILayout.Space(2f);
        }

        /// <summary>
        /// Builds the status line's text and colour from what the transport runs while playing.
        /// </summary>
        private void RefreshStatus()
        {
            _steamWarningText = Application.isPlaying && FacepunchSockets.IsSteamShutDown() ? "Steam is not initialized, so the transport cannot start. The game calls SteamClient.Init, or SteamServer.Init for a dedicated server, before starting; this component never does." : string.Empty;

            if (!Application.isPlaying || target is not FacepunchTransport { Transport: FacepunchSockets facepunchSockets })
            {
                _statusText = Application.isPlaying ? "Not added to a running CoreManager" : "Ready to start";
                _statusColor = IdleColor;

                return;
            }

            TransportManager transportManager = facepunchSockets.CoreManager.TransportManager;
            bool isServerStarted = transportManager.IsServerStarted;
            bool isClientStarted = transportManager.IsClientStarted;

            if (!isServerStarted && !isClientStarted)
            {
                _statusText = "Steam transport idle";
                _statusColor = IdleColor;

                return;
            }

            string roleText = isServerStarted && isClientStarted ? "hosting" : isServerStarted ? "server" : "client";

            _statusText = SteamClient.IsValid ? $"Steam running · App {SteamClient.AppId.Value} · {roleText}" : $"Steam running · {roleText}";
            _statusColor = RunningColor;
        }

        /// <summary>
        /// Draws the Connection group: the addressing and the fields it decides.
        /// </summary>
        private void DrawConnectionGroup()
        {
            SerializedProperty addressingSerializedProperty = serializedObject.FindProperty("_addressing");
            FacepunchAddressing addressing = (FacepunchAddressing)addressingSerializedProperty.enumValueIndex;

            DrawHeader("Connection");
            EditorGUILayout.PropertyField(addressingSerializedProperty);

            using (new EditorGUI.IndentLevelScope())
            {
                SerializedProperty targetSerializedProperty = serializedObject.FindProperty(addressing is FacepunchAddressing.SteamId ? "_serverSteamId" : "_serverAddress");

                EditorGUILayout.PropertyField(targetSerializedProperty, new GUIContent(GetTargetLabel(addressing), targetSerializedProperty.tooltip));

                // A fake address carries the port Steam granted, so only the other two addressings have a port to agree on.
                if (addressing is not FacepunchAddressing.FakeIp)
                {
                    SerializedProperty portSerializedProperty = serializedObject.FindProperty("_port");

                    EditorGUILayout.PropertyField(portSerializedProperty, new GUIContent(addressing is FacepunchAddressing.SteamId ? "Virtual Port" : "Port", portSerializedProperty.tooltip));
                }

                if (addressing is FacepunchAddressing.IpAddress)
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("_bindAddress"));
            }
        }

        /// <summary>
        /// Draws the Session group: who may join and what a client may send.
        /// </summary>
        private void DrawSessionGroup()
        {
            SerializedProperty maximumConnectionsSerializedProperty = serializedObject.FindProperty("_maximumConnections");
            SerializedProperty ticketAuthenticationEnabledSerializedProperty = serializedObject.FindProperty("_ticketAuthenticationEnabled");
            SerializedProperty shareSteamIdsEnabledSerializedProperty = serializedObject.FindProperty("_shareSteamIdsEnabled");

            DrawHeader("Session");
            EditorGUILayout.PropertyField(maximumConnectionsSerializedProperty);

            // Corrected only while the selection agrees, so one object's value is never written over the others.
            if (!maximumConnectionsSerializedProperty.hasMultipleDifferentValues && maximumConnectionsSerializedProperty.uintValue < FacepunchTransport.MinimumMaximumConnections)
                maximumConnectionsSerializedProperty.uintValue = FacepunchTransport.MinimumMaximumConnections;

            DrawKibibyteField(serializedObject.FindProperty("_maximumClientPacketBytes"), "Maximum Client Packet", "KiB");
            EditorGUILayout.PropertyField(ticketAuthenticationEnabledSerializedProperty, new GUIContent("Ticket Authentication", ticketAuthenticationEnabledSerializedProperty.tooltip));
            EditorGUILayout.PropertyField(shareSteamIdsEnabledSerializedProperty, new GUIContent("Share Steam IDs", shareSteamIdsEnabledSerializedProperty.tooltip));
        }

        /// <summary>
        /// Draws the collapsed Advanced section with the receive mode and Steam's timeouts, send rate and buffer.
        /// </summary>
        private void DrawAdvancedSection()
        {
            SerializedProperty connectingTimeoutSecondsSerializedProperty = serializedObject.FindProperty("_connectingTimeoutSeconds");
            SerializedProperty idleTimeoutSecondsSerializedProperty = serializedObject.FindProperty("_idleTimeoutSeconds");
            SerializedProperty sendRateMinimumSerializedProperty = serializedObject.FindProperty("_sendRateMinimumBytesPerSecond");
            SerializedProperty sendRateMaximumSerializedProperty = serializedObject.FindProperty("_sendRateMaximumBytesPerSecond");
            SerializedProperty sendBufferSizeSerializedProperty = serializedObject.FindProperty("_sendBufferSizeBytes");
            SerializedProperty perConnectionReceiveEnabledSerializedProperty = serializedObject.FindProperty("_perConnectionReceiveEnabled");
            string receiveSummary = perConnectionReceiveEnabledSerializedProperty.boolValue ? "Per-connection · " : string.Empty;
            string advancedSummary = $"{receiveSummary}{FormatSeconds(connectingTimeoutSecondsSerializedProperty.intValue)} / {FormatSeconds(idleTimeoutSecondsSerializedProperty.intValue)} · {FormatBytes(sendRateMinimumSerializedProperty.uintValue, SteamDefaultSendRateBytes)} to {FormatBytes(sendRateMaximumSerializedProperty.uintValue, SteamDefaultSendRateBytes)}/s · {FormatBytes(sendBufferSizeSerializedProperty.uintValue, SteamDefaultSendBufferBytes)} buffer";

            EditorGUILayout.Space(GroupSpacing);

            if (!DrawSection("Advanced", advancedSummary, isExpandedByDefault: false))
                return;

            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUILayout.PropertyField(perConnectionReceiveEnabledSerializedProperty, new GUIContent("Per-Connection Receive", perConnectionReceiveEnabledSerializedProperty.tooltip));
                DrawPropertyFieldWithUnit(connectingTimeoutSecondsSerializedProperty, "Connecting Timeout", "s");
                DrawPropertyFieldWithUnit(idleTimeoutSecondsSerializedProperty, "Idle Timeout", "s");
                DrawKibibyteField(sendRateMinimumSerializedProperty, "Minimum Send Rate", "KiB/s");
                DrawKibibyteField(sendRateMaximumSerializedProperty, "Maximum Send Rate", "KiB/s");
                DrawKibibyteField(sendBufferSizeSerializedProperty, "Send Buffer Size", "KiB");
            }

            EditorGUILayout.HelpBox("Timeouts and send settings apply to every Steam connection in this process, because Facepunch has no per-connection options.", MessageType.None);

            uint sendRateMinimumBytes = sendRateMinimumSerializedProperty.uintValue;
            uint sendRateMaximumBytes = sendRateMaximumSerializedProperty.uintValue;

            if (sendRateMinimumBytes is not UnsetSendSetting && sendRateMaximumBytes is not UnsetSendSetting && sendRateMinimumBytes > sendRateMaximumBytes)
                EditorGUILayout.HelpBox("The minimum send rate is above the maximum. Lower it, or raise the maximum to match.", MessageType.Warning);

            uint sendBufferSizeBytes = sendBufferSizeSerializedProperty.uintValue;

            if (!sendBufferSizeSerializedProperty.hasMultipleDifferentValues && sendBufferSizeBytes is not UnsetSendSetting && sendBufferSizeBytes < SteamMessagePieceBytes)
                EditorGUILayout.HelpBox("Reliable messages are sent in pieces of up to 512 KiB, and one piece larger than the send buffer closes the link. Keep it at 512 KiB or more, or zero to leave Steam's value alone.", MessageType.Warning);
        }

        /// <summary>
        /// Reads how clients reach this peer's server, for the row under the status box.
        /// </summary>
        private void RefreshRuntime()
        {
            _serverRowLabel = string.Empty;
            _serverRowText = string.Empty;
            _serverRowCopyText = null;

            if (target is not FacepunchTransport { Transport: FacepunchSockets facepunchSockets } || !facepunchSockets.CoreManager.TransportManager.IsServerStarted || FacepunchSockets.IsSteamShutDown())
                return;

            switch (facepunchSockets.Addressing)
            {
                case FacepunchAddressing.IpAddress:
                    // Clients need this machine's public IP address, which only the network knows, so only the port is shown.
                    _serverRowLabel = "Server's Port";
                    _serverRowText = $"UDP {facepunchSockets.Configuration.Port}";

                    break;
                case FacepunchAddressing.FakeIp:
                    _serverRowLabel = "Server's Fake Address";
                    _serverRowText = facepunchSockets.ServerFakeAddress ?? "Waiting for Steam to grant one";
                    _serverRowCopyText = facepunchSockets.ServerFakeAddress;

                    break;
                default:
                    _serverRowLabel = "Server's SteamID";
                    _serverRowText = FacepunchSockets.GetOwnSteamId().ToString();
                    _serverRowCopyText = _serverRowText;

                    break;
            }
        }

        /// <summary>
        /// Draws, while a server runs, the row naming how clients reach it, with a button that copies just the SteamID or fake address.
        /// </summary>
        private void DrawServerRow()
        {
            if (_serverRowLabel.Length == 0)
                return;

            Rect rowRect = EditorGUILayout.GetControlRect();
            float buttonWidth = _serverRowCopyText is null ? 0f : CopyButtonWidth + 4f;

            EditorGUI.LabelField(new Rect(rowRect.x, rowRect.y, rowRect.width - buttonWidth, rowRect.height), _serverRowLabel, _serverRowText);

            if (_serverRowCopyText is not null && GUI.Button(new Rect(rowRect.xMax - CopyButtonWidth, rowRect.y, CopyButtonWidth, rowRect.height), "Copy", EditorStyles.miniButton))
                EditorGUIUtility.systemCopyBuffer = _serverRowCopyText;
        }

        /// <summary>
        /// Draws a group's header in bold.
        /// </summary>
        /// <param name="title">The group's name.</param>
        private static void DrawHeader(string title)
        {
            EditorGUILayout.Space(GroupSpacing);
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
        }

        /// <summary>
        /// Draws a section's title as a bold foldout, with its summary in the value column while it is collapsed.
        /// </summary>
        /// <param name="title">The section's name, which also keys its stored state.</param>
        /// <param name="summary">A one-line reading of the section's values.</param>
        /// <param name="isExpandedByDefault">Whether the section starts expanded before anyone has toggled it.</param>
        /// <returns><see langword="true"/> when the section is expanded and its contents should be drawn.</returns>
        private static bool DrawSection(string title, string summary, bool isExpandedByDefault = true)
        {
            string sessionKey = $"{SectionExpandedSessionKeyPrefix}{title}";
            bool isExpanded = SessionState.GetBool(sessionKey, isExpandedByDefault);
            Rect rowRect = EditorGUILayout.GetControlRect();
            bool isNowExpanded = EditorGUI.Foldout(rowRect, isExpanded, GUIContent.none, toggleOnLabelClick: true);
            Rect titleRect = new(rowRect.x + FoldoutArrowWidth, rowRect.y, EditorGUIUtility.labelWidth - FoldoutArrowWidth, rowRect.height);

            EditorGUI.LabelField(titleRect, title, EditorStyles.boldLabel);

            if (!isNowExpanded)
                EditorGUI.LabelField(new Rect(rowRect.x + EditorGUIUtility.labelWidth + 2f, rowRect.y, rowRect.width - EditorGUIUtility.labelWidth - 2f, rowRect.height), summary, EditorStyles.miniLabel);

            if (isNowExpanded != isExpanded)
                SessionState.SetBool(sessionKey, isNowExpanded);

            return isNowExpanded;
        }

        /// <summary>
        /// Draws a property under its own label, with its unit at the right of the field.
        /// </summary>
        /// <param name="serializedProperty">The property to draw.</param>
        /// <param name="label">The label, used in place of the property's name.</param>
        /// <param name="unit">The unit, such as "s".</param>
        private static void DrawPropertyFieldWithUnit(SerializedProperty serializedProperty, string label, string unit)
        {
            Rect rowRect = EditorGUILayout.GetControlRect();

            EditorGUI.PropertyField(rowRect, serializedProperty, new GUIContent(label, serializedProperty.tooltip));
            DrawUnit(rowRect, unit);
        }

        /// <summary>
        /// Draws a byte count entered in kibibytes, with its unit at the right of the field.
        /// </summary>
        /// <param name="bytesSerializedProperty">The <see cref="uint"/> property holding the count in bytes.</param>
        /// <param name="label">The label, used in place of the property's name.</param>
        /// <param name="unit">The unit, such as "KiB/s".</param>
        private static void DrawKibibyteField(SerializedProperty bytesSerializedProperty, string label, string unit)
        {
            Rect rowRect = EditorGUILayout.GetControlRect();

            using (EditorGUI.PropertyScope propertyScope = new(rowRect, new GUIContent(label, bytesSerializedProperty.tooltip), bytesSerializedProperty))
            {
                EditorGUI.BeginChangeCheck();
                EditorGUI.showMixedValue = bytesSerializedProperty.hasMultipleDifferentValues;

                long kibibytes = EditorGUI.LongField(rowRect, propertyScope.content, bytesSerializedProperty.uintValue / KibibyteBytes);

                EditorGUI.showMixedValue = false;

                if (EditorGUI.EndChangeCheck())
                    bytesSerializedProperty.uintValue = (uint)Math.Clamp(kibibytes, 0L, uint.MaxValue / KibibyteBytes) * KibibyteBytes;
            }

            DrawUnit(rowRect, unit);
        }

        /// <summary>
        /// Draws a unit at the right of a field already drawn in a row.
        /// </summary>
        /// <param name="rowRect">The row the field was drawn in.</param>
        /// <param name="unit">The unit.</param>
        private static void DrawUnit(Rect rowRect, string unit)
        {
            GUIContent unitGuiContent = new(unit);
            float unitWidth = EditorStyles.miniLabel.CalcSize(unitGuiContent).x;

            GUI.Label(new Rect(rowRect.xMax - unitWidth - UnitPadding, rowRect.y, unitWidth, rowRect.height), unitGuiContent, EditorStyles.miniLabel);
        }

        /// <summary>
        /// Gets the label of the field a client connects to.
        /// </summary>
        /// <param name="addressing">The addressing being drawn.</param>
        /// <returns>The label.</returns>
        private static string GetTargetLabel(FacepunchAddressing addressing)
        {
            return addressing switch
            {
                FacepunchAddressing.IpAddress => "Server's Address",
                FacepunchAddressing.FakeIp => "Server's Fake Address",
                _ => "Server's SteamID"
            };
        }

        /// <summary>
        /// Writes a send setting's byte count in the largest whole unit that reads cleanly.
        /// </summary>
        /// <param name="bytes">The count, or <see cref="UnsetSendSetting"/> for Steam's default.</param>
        /// <param name="steamDefaultBytes">The count Steam uses by default.</param>
        /// <returns>The count, such as "4 MiB" or "300 KiB".</returns>
        private static string FormatBytes(uint bytes, uint steamDefaultBytes)
        {
            if (bytes is UnsetSendSetting)
                bytes = steamDefaultBytes;

            if (bytes >= 1024U * 1024U && bytes % (1024U * 1024U) == 0U)
                return $"{bytes / (1024U * 1024U)} MiB";

            return bytes >= 1024U ? $"{bytes / 1024U} KiB" : $"{bytes} B";
        }

        /// <summary>
        /// Writes a timeout in seconds, reading zero as Steam's default.
        /// </summary>
        /// <param name="seconds">The timeout, or zero for Steam's default.</param>
        /// <returns>The timeout, such as "15 s".</returns>
        private static string FormatSeconds(int seconds) => $"{(seconds == 0 ? SteamDefaultTimeoutSeconds : seconds)} s";
    }
}
