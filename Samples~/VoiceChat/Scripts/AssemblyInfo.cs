using Nucleus.Serializers;

// The Voice Chat assembly declares its voice call, so it needs a bundle of its own, clear of the transport's (490), its test
// assembly's (491), the shared sample's (492) and the test harness's (493).
[assembly: NetworkBundle((ushort)(NetworkTypeRegistry.FrameworkBundleId - 494))]
