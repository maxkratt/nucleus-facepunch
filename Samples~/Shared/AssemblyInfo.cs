using Nucleus.Serializers;

// The shared sample assembly declares the name tag's component, so it needs a bundle of its own, clear of the transport's (484), its
// test assembly's (485) and the test harness's (487).
[assembly: NetworkBundle((ushort)(NetworkTypeRegistry.FrameworkBundleId - 486))]
