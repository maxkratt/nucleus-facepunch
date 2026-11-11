using System.Runtime.CompilerServices;
using Nucleus.Serializers;

// The inspector reads whether Steam is running through the transport's own check.
[assembly: InternalsVisibleTo("Nucleus.Integrations.Facepunch.Editor")]
// The play mode tests read the transport's internals.
[assembly: InternalsVisibleTo("Nucleus.Integrations.Facepunch.Tests")]

// A game's main assembly defaults to bundle 0, so this one takes an id near the top of the space, clear of the Unity integration's own ids.
[assembly: NetworkBundle((ushort)(NetworkTypeRegistry.FrameworkBundleId - 490))]
