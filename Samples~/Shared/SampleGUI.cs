using UnityEngine;

namespace Nucleus.Integrations.Facepunch.Samples
{
    /// <summary>
    /// The few drawing helpers the sample panels share, so each panel's own code stays about Steam and Nucleus rather than layout.
    /// </summary>
    /// <remarks>
    /// Everything else a panel draws is a plain <see cref="GUILayout"/> call, which the panel's skin styles. A skin without the named
    /// styles used here draws each element in its plain form instead.
    /// </remarks>
    public static class SampleGUI
    {
        /// <summary>
        /// The skin style a panel's title draws with.
        /// </summary>
        private const string TitleStyleName = "Title";
        /// <summary>
        /// The skin style a section heading's text draws with.
        /// </summary>
        private const string SectionStyleName = "Section";
        /// <summary>
        /// The skin style of the divider left of a section heading.
        /// </summary>
        private const string DividerLeftStyleName = "DividerLeft";
        /// <summary>
        /// The skin style of the divider right of a section heading.
        /// </summary>
        private const string DividerRightStyleName = "DividerRight";
        /// <summary>
        /// The skin style a row's or a field's label draws with, left aligned.
        /// </summary>
        private const string RowStyleName = "Row";
        /// <summary>
        /// The skin style a row's badge draws with.
        /// </summary>
        private const string BadgeStyleName = "Badge";
        /// <summary>
        /// The distance of a panel from the screen's top left corner, in pixels.
        /// </summary>
        private const float PanelInset = 10f;
        /// <summary>
        /// The height of a row holding a button or a text field, in pixels, so its label and avatar centre on the control.
        /// </summary>
        private const float ControlRowHeight = 38f;
        /// <summary>
        /// The height of a row without a control, in pixels.
        /// </summary>
        private const float PlainRowHeight = 30f;
        /// <summary>
        /// The side of an avatar drawn in a row, in pixels.
        /// </summary>
        private const float AvatarSize = 24f;
        /// <summary>
        /// The width of a field's label, in pixels.
        /// </summary>
        private const float FieldLabelWidth = 96f;
        /// <summary>
        /// The width of a row's button, in pixels.
        /// </summary>
        private const float RowButtonWidth = 70f;

        /// <summary>
        /// Starts a panel at the screen's top left: sets the skin, opens the panel's box and draws its title. Close it with
        /// <see cref="EndPanel"/>.
        /// </summary>
        /// <param name="guiSkin">The skin the panel draws with, or <see langword="null"/> for Unity's default look.</param>
        /// <param name="width">The panel's width, in pixels.</param>
        /// <param name="title">The panel's title.</param>
        public static void BeginPanel(GUISkin guiSkin, float width, string title)
        {
            GUI.skin = guiSkin;
            GUILayout.BeginArea(new(PanelInset, PanelInset, width, Screen.height - PanelInset * 2f));
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label(title, GUI.skin.FindStyle(TitleStyleName) ?? GUI.skin.label);
        }

        /// <summary>
        /// Closes the panel <see cref="BeginPanel"/> opened.
        /// </summary>
        public static void EndPanel()
        {
            GUILayout.EndVertical();
            GUILayout.EndArea();
        }

        /// <summary>
        /// Draws a section heading between two dividers.
        /// </summary>
        /// <param name="heading">The heading's text.</param>
        public static void Section(string heading)
        {
            GUIStyle dividerLeftStyle = GUI.skin.FindStyle(DividerLeftStyleName);
            GUIStyle dividerRightStyle = GUI.skin.FindStyle(DividerRightStyleName);
            GUILayout.BeginHorizontal();

            if (dividerLeftStyle is not null)
                GUILayout.Box(GUIContent.none, dividerLeftStyle);

            GUILayout.Label(heading, GUI.skin.FindStyle(SectionStyleName) ?? GUI.skin.label);

            if (dividerRightStyle is not null)
                GUILayout.Box(GUIContent.none, dividerRightStyle);

            GUILayout.EndHorizontal();
        }

        /// <summary>
        /// Draws a text field with its label beside it.
        /// </summary>
        /// <param name="label">The label.</param>
        /// <param name="text">The field's current text.</param>
        /// <returns>The field's text after this pass.</returns>
        public static string Field(string label, string text)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUI.skin.FindStyle(RowStyleName) ?? GUI.skin.label, GUILayout.Width(FieldLabelWidth), GUILayout.Height(ControlRowHeight));
            string editedText = GUILayout.TextField(text);
            GUILayout.EndHorizontal();

            return editedText;
        }

        /// <summary>
        /// Draws one row of a player list: an avatar, a name, and on the right either a badge or a button.
        /// </summary>
        /// <param name="avatar">The player's avatar, or <see langword="null"/> to leave its place empty while it loads.</param>
        /// <param name="name">The player's name.</param>
        /// <param name="badge">A short tag drawn on the right, such as HOST, or <see langword="null"/> for none.</param>
        /// <param name="buttonText">The text of a button drawn on the right, or <see langword="null"/> for none.</param>
        /// <returns><see langword="true"/> when the row's button was clicked this pass.</returns>
        public static bool PlayerRow(Texture avatar, string name, string badge = null, string buttonText = null)
        {
            float rowHeight = buttonText is null ? PlainRowHeight : ControlRowHeight;
            GUIStyle rowStyle = GUI.skin.FindStyle(RowStyleName) ?? GUI.skin.label;
            RectOffset nameMargin = rowStyle.margin;
            GUILayout.BeginHorizontal();

            // The avatar sits in a column as tall as the name with its margins, centred by the space either side of it, so the two
            // share a centre line.
            GUILayout.BeginVertical(GUILayout.Width(AvatarSize), GUILayout.Height(rowHeight + nameMargin.top + nameMargin.bottom));
            GUILayout.Space(nameMargin.top);
            GUILayout.FlexibleSpace();

            if (avatar != null)
                GUILayout.Box(avatar, GUIStyle.none, GUILayout.Width(AvatarSize), GUILayout.Height(AvatarSize));
            else
                GUILayout.Space(AvatarSize);

            GUILayout.FlexibleSpace();
            GUILayout.Space(nameMargin.bottom);
            GUILayout.EndVertical();

            GUILayout.Label(name, rowStyle, GUILayout.Height(rowHeight));
            GUILayout.FlexibleSpace();

            if (badge is not null)
                GUILayout.Label(badge, GUI.skin.FindStyle(BadgeStyleName) ?? GUI.skin.label, GUILayout.Height(rowHeight));

            bool isClicked = buttonText is not null && GUILayout.Button(buttonText, GUILayout.Width(RowButtonWidth));
            GUILayout.EndHorizontal();

            return isClicked;
        }
    }
}
