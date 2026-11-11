using Nucleus.Serializers;

// The Voice Chat assembly declares its voice call, so it needs a bundle of its own, clear of the transport's (484), its test
// assembly's (485), the shared sample's (486) and the test harness's (487).
[assembly: NetworkBundle((ushort)(NetworkTypeRegistry.FrameworkBundleId - 488))]
