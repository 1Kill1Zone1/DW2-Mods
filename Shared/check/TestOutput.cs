using System;
using System.IO;

// Native test inputs stay in their original location; generated results belong to one run.
internal static class TestOutput
{
    public static string Root
    {
        get
        {
            string path = Environment.GetEnvironmentVariable("DW2_TEST_OUTPUT_DIR")
                ?? throw new InvalidOperationException("Run checks through scripts/run-check.ps1 to isolate generated files.");
            if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path))
                throw new InvalidOperationException("Missing test-run output directory.");
            return path;
        }
    }
}
