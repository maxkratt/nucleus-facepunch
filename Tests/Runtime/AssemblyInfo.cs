using Nucleus.Serializers;

// The test assembly declares its own network types, so it takes a bundle id of its own, one below the transport's.
[assembly: NetworkBundle((ushort)(NetworkTypeRegistry.FrameworkBundleId - 491))]
