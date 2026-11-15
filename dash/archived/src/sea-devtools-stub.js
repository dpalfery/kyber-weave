// Stub for ink's optional react-devtools-core dependency, used only by the SEA
// build. ink imports it unconditionally at module scope but reaches it only
// when DEV is set; the package is not a dependency of this project, and a bare
// specifier cannot resolve inside the SEA's data: URL module scope anyway.
export function connectToDevTools() {}
export default { connectToDevTools }
