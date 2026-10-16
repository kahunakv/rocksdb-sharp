using System;
using System.IO;

namespace Tests;

internal static class TestDb
{
    /// <summary>Runs <paramref name="body"/> with a fresh directory path, and deletes the directory afterwards.</summary>
    public static void WithPath(Action<string> body)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            body(path);
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
    }
}
