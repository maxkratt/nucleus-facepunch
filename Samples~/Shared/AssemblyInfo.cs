using Nucleus.Serializers;

// The shared sample assembly declares the name tag's component, so it needs a bundle of its own, clear of the transport's (490), its
// test assembly's (491) and the test harness's (493).
[assembly: NetworkBundle((ushort)(NetworkTypeRegistry.FrameworkBundleId - 492))]
