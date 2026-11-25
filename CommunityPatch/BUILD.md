# Building Community Patch

Set the MSBuild property GameRoot to your own Steam Beta 1.3.8.8 installation. Build Source/CommunityPatch/CommunityPatch.csproj and each of the ten Source/Modules projects with the .NET 8 SDK in Release configuration. Use the native assemblies from your own game. Place the bootstrap DLL at the package root and each module DLL in modules/<module id>/. For local rebuilds update each modules.json SHA-256 entry to the digest of the DLL you actually built. The released ZIP already contains the verified binaries. Game assemblies and campaign fixtures are supplied by the tester.

The check project under Source/CommunityPatch/check accepts an isolated loader plan and fixture. The salvage regression project uses a dedicated integration fixture and its object IDs. Set DW2_TEST_MATRIX_DLL and DW2_TEST_OBSERVED_HOOKS for its Matrix provider and hook report.
