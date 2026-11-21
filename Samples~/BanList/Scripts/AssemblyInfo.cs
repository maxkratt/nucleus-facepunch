using Nucleus.Serializers;

// This sample declares its ban notice, so it needs a bundle of its own, clear of the transport's (490), its tests' (491), the shared
// sample's (492), the test harness's (493) and the Voice Chat sample's (494).
[assembly: NetworkBundle((ushort)(NetworkTypeRegistry.FrameworkBundleId - 495))]
