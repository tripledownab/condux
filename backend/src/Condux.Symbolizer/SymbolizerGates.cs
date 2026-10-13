using Condux.Core.Http;

namespace Condux.Symbolizer;

/// <summary>How many files the symbolizer takes in at once. Each upload holds its body, and growing the
/// buffer briefly takes about twice the file, so a few places bound the memory whatever the callers do.</summary>
internal sealed class FileUploadGate() : AdmissionGate(2);

/// <summary>How many llvm-symbolizer runs the service starts at once. Each run loads the whole debug file,
/// so the count bounds memory and CPU across however many callers there are.</summary>
internal sealed class ResolveGate() : AdmissionGate(4);
