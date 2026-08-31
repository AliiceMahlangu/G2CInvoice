using System;
using System.IO;
using System.Text.Json;

namespace G2C.Invoice
{
    /// <summary>
    /// Persists a sequential invoice DocumentNumber counter across runs.
    /// Sage enforces global uniqueness on DocumentNumber (confirmed by the
    /// "Document Number already exists" error on a rerun), and this tool
    /// restarts fresh every month with no server/database, so the "next
    /// number" has to be saved to disk between runs.
    ///
    /// A number is never reused: the counter is persisted to disk the
    /// moment a number is handed out, before the caller attempts to post
    /// it - so even a failed post or a mid-run crash doesn't cause the same
    /// number to be issued twice.
    /// </summary>
    internal static class DocumentNumberSequence
    {
        private const string Prefix = "INV";
        private const int StartingNumber = 105676;

        private static readonly string CounterFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "G2CInvoice",
            "document_number_counter.json");

        private class StoredCounter
        {
            public int NextNumber { get; set; }
        }

        private static int _nextNumber = -1;

        private static void EnsureLoaded()
        {
            if (_nextNumber >= 0)
                return;

            try
            {
                if (File.Exists(CounterFilePath))
                {
                    var json = File.ReadAllText(CounterFilePath);
                    var data = JsonSerializer.Deserialize<StoredCounter>(json);
                    if (data != null && data.NextNumber > 0)
                    {
                        _nextNumber = data.NextNumber;
                        Console.WriteLine($"Document number sequence loaded, next is {Prefix}{_nextNumber}.");
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WARNING: could not read document number counter, starting fresh: {ex.Message}");
            } 

            _nextNumber = StartingNumber;
            Console.WriteLine($"No saved document number sequence found — starting at {Prefix}{_nextNumber}.");
        }

        private static void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(CounterFilePath);
                Directory.CreateDirectory(dir);
                File.WriteAllText(CounterFilePath, JsonSerializer.Serialize(new StoredCounter { NextNumber = _nextNumber }));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WARNING: could not save document number counter — the next run may reuse this number: {ex.Message}");
            }
        }

        /// <summary>
        /// Returns the next document number (e.g. "INV105676"). Persists the
        /// incremented counter immediately, before the caller does anything
        /// with the returned value.
        /// </summary>
        public static string Next()
        {
            EnsureLoaded();

            var number = $"{Prefix}{_nextNumber}";
            _nextNumber++;
            Save();

            return number;
        }
    }
}