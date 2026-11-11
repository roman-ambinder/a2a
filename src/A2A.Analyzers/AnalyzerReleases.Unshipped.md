; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
A2A0001 | A2A.Hosting | Warning | A2ASetupAnalyzer: A2A endpoints are mapped but UseA2A() is never called
A2A0002 | A2A.Hosting | Warning | A2ASetupAnalyzer: A2A endpoints are mapped without authorization
A2A0003 | A2A.Hosting | Warning | A2ASetupAnalyzer: UseA2A() runs before authentication or after authorization
