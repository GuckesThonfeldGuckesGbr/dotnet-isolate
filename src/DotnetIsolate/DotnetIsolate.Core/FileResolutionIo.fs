/// Real MSBuild-backed implementation of FileResolution.ProjectItemsResolver, deliberately kept
/// in its own file rather than inside FileResolution.fs: coverlet's coverage-exclude filter only
/// works at whole-class granularity, and putting IO code in the same module as pure logic would
/// force choosing between under-covering the unit gate (QP-5) or, via `[<ExcludeFromCodeCoverage>]`,
/// silently excluding this code from the integration gate (QP-4) too - verified directly that the
/// attribute applies to every coverage run against the assembly, not just a scoped one, which
/// would make QP-4 blind to exactly the code it's meant to validate. A separate file lets the
/// unit-test coverage command exclude `FileResolutionIo` by class name while the integration-test
/// coverage command measures it normally.
module DotnetIsolate.Core.FileResolutionIo

/// The MSBuild item types that make up a project's build-relevant files (FR-4). ProjectReference
/// is resolved separately by ProjectGraph/MsBuild.projectReferenceResolver.
let fileItemTypes = FileResolution.fileItemTypes

/// A `FileResolution.ProjectItemsResolver` backed by real MSBuild evaluation.
let projectItemsResolver: FileResolution.ProjectItemsResolver =
    fun projectPath -> MsBuild.getItems projectPath fileItemTypes
