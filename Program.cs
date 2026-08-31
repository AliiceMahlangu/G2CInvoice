using G2C.Invoice.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace G2C.Invoice
{
    /// <summary>
    /// Central place for every folder the app reads from or writes to.
    /// Everything lives under a "G2CInvoice" folder on the current user's
    /// Desktop, so it's easy to find and drop files into by hand:
    ///   - input, output, processed, and log files are no longer mixed in
    ///     with the build output in bin\Debug
    ///   - it's a plain, visible folder rather than a hidden system path,
    ///     matching how the .csv token store under %LocalAppData% is
    ///     already scoped to the current Windows user
    ///
    /// NOTE: because this is scoped to the logged-in user's Desktop, the
    /// app must run as the same Windows user every time (same as the
    /// existing token.json under %LocalAppData%). If this is ever run
    /// under a different account (e.g. a scheduled task configured for a
    /// service account), it will look for a Desktop folder under that
    /// account instead, not yours.
    ///
    /// All directories are created (if missing) once at startup via
    /// EnsureFoldersExist(), so nothing downstream needs to check.
    /// </summary>
    internal static class AppPaths
    {
        public static readonly string Base = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "G2CInvoice");

        // Where JHB1/JHB2 payment export CSVs and G2CAccounts.csv are read from.
        public static readonly string Input = Path.Combine(Base, "Input");

        // Where source files are archived after a fully successful run.
        public static readonly string Processed = Path.Combine(Base, "Processed");

        // Where source files are archived if anything about that brand's
        // run failed (read error or any invoice not Posted).
        public static readonly string Error = Path.Combine(Base, "Error");

        // Where the generated "Invoices JHB1/JHB2 ..." result CSVs are written.
        public static readonly string OutputInvoices = Path.Combine(Base, "Output", "Invoices");

        // Where RunLog_*.log files are written.
        public static readonly string Logs = Path.Combine(Base, "Logs");

        public static void EnsureFoldersExist()
        {
            Directory.CreateDirectory(Input);
            Directory.CreateDirectory(Processed);
            Directory.CreateDirectory(Error);
            Directory.CreateDirectory(OutputInvoices);
            Directory.CreateDirectory(Logs);
        }
    }

    internal static class Program
    {
        private static List<Account> Accounts = new List<Account>();
        private static List<InvoiceShort> Invoices = new List<InvoiceShort>();

        // Which invoice number came from which brand's source file, tracked
        // independently of Account resolution so the output log (and the
        // Processed/Error file move) can still attribute a skipped/failed
        // invoice to the right brand even if its account never resolved.
        private static readonly Dictionary<string, string> InvoiceSourceBrand =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // One entry per brand: the brand value as it appears in the CSV
        // "Brand" column, a short label for output filenames, and candidate
        // wildcard patterns to locate this month's file. Real filenames seen
        // so far are inconsistent ("JHB_1_payments_Aug_2025.csv" vs
        // "JHB2_payments_Aug_2025.csv"), so each brand tries multiple
        // patterns and uses the first (most recently modified) match.
        // "JHB2 payments Aug 2025.csv" (space-separated) is confirmed as the
        // real naming convention from a successful run. Kept the
        // underscore/no-separator variants too in case naming is ever
        // inconsistent between brands or months.
        private static readonly (string Brand, string Label, string[] FilePatterns)[] BrandFiles =
        {
            ("jhb1.go2cloud.co.za", "JHB1", new[] { "JHB1*payments*.csv", "JHB 1*payments*.csv", "JHB_1*payments*.csv", "JHB*1_payments*.csv" }),
            ("jhb2.go2cloud.co.za", "JHB2", new[] { "JHB2*payments*.csv", "JHB 2*payments*.csv", "JHB_2*payments*.csv", "JHB*2_payments*.csv" }),
        };

        // Master Pastel account-code lookup: Full Name, Pastel Account Code,
        // UUID. Used to attach each account's Pastel code so it can be sent
        // to Sage as the invoice Reference for reconciliation. This is a
        // persistent reference list, not a monthly batch file, so it is
        // never moved to Processed/Error. Lives in AppPaths.Input alongside
        // the monthly payment files.
        private const string PastelAccountsFileName = "G2CAccounts.csv";

        // Full path of each brand's source file for this run, recorded so it
        // can be archived after posting completes.
        private static readonly Dictionary<string, string> BrandFilePaths =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Brands whose source file failed to read entirely (e.g. malformed
        // CSV) — always routed to the Error folder regardless of posting
        // results, since no invoices could even be attempted for them.
        private static readonly HashSet<string> BrandsWithReadErrors =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static StreamWriter _logWriter;

        private static async Task Main(string[] args)
        {
            await MainAsync(args);
        }

        private static async Task MainAsync(string[] args)
        {
            AppPaths.EnsureFoldersExist();
            StartLogging();

            Console.WriteLine("=== G2C Invoice — Sage SA Sandbox Integration ===");
            Console.WriteLine($"Run started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine($"Data folder: {AppPaths.Base}");
            Console.WriteLine();

            try
            {
                SageAuth.Validate();

                // Uses saved/refreshed tokens when available and only falls
                // back to an interactive browser login (auto-captured via
                // loopback listener, no manual paste) when there's no valid
                // saved refresh token.
                var authenticated = await SageAuth.EnsureAuthenticatedAsync();
                if (!authenticated)
                {
                    Console.WriteLine("\nAuthentication failed. Exiting.");
                    return;
                }

                ReadPaymentFiles();

                var client = new SageClient();

                await client.ResolveCompanyIdAsync();
                await client.LoadAccountsAsync(Accounts);
                await client.LoadTaxTypesAsync();
                await client.ResolveCustomersAsync(Accounts);

                var results = await client.PostInvoicesAsync(Invoices, Accounts);

                WriteInvoiceLogs(results);
                ArchiveSourceFiles(results);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\nFATAL ERROR: {ex}");
            }
            finally
            {
                Console.WriteLine($"\nRun finished: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                StopLogging();
            }
        }

        // ---------- Logging: mirror all console output to a log file ----------

        private static void StartLogging()
        {
            try
            {
                var logPath = Path.Combine(AppPaths.Logs, $"RunLog_{DateTime.Now:yyyy-MM-dd_HHmmss}.log");
                _logWriter = new StreamWriter(logPath, append: false) { AutoFlush = true };
                Console.SetOut(new DualTextWriter(Console.Out, _logWriter));
            }
            catch (Exception ex)
            {
                // If the log file can't be created, keep going with console
                // output only rather than aborting the whole run.
                Console.WriteLine($"WARNING: could not start log file: {ex.Message}");
            }
        }

        private static void StopLogging()
        {
            try
            {
                _logWriter?.Flush();
                _logWriter?.Dispose();
            }
            catch
            {
                // Ignore - not critical at shutdown.
            }
        }

        private class DualTextWriter : TextWriter
        {
            private readonly TextWriter _first;
            private readonly TextWriter _second;

            public DualTextWriter(TextWriter first, TextWriter second)
            {
                _first = first;
                _second = second;
            }

            public override Encoding Encoding => _first.Encoding;

            public override void Write(char value)
            {
                _first.Write(value);
                _second.Write(value);
            }

            public override void Write(string value)
            {
                _first.Write(value);
                _second.Write(value);
            }

            public override void WriteLine(string value)
            {
                _first.WriteLine(value);
                _second.WriteLine(value);
            }
        }

        // ---------- Reading: both JHB1 and JHB2 payment exports ----------
        // Both files share the same Go2Cloud export header row (Brand,
        // Account Manager, User ID, Company, ..., Tax Ref, ..., Invoice,
        // Starttime, Value, Total, ...). Every row in both files is:
        //   - a source of one Account (deduped by User ID across both files)
        //   - an invoice line to post to Sage

        private static void ReadPaymentFiles()
        {
            var pastelCodesByUuid = ReadPastelAccountCodes();

            var accountsByUuid = new Dictionary<string, Account>(StringComparer.OrdinalIgnoreCase);

            foreach (var (brand, label, patterns) in BrandFiles)
            {
                string filePath;
                try
                {
                    filePath = FindBrandFile(patterns);
                    BrandFilePaths[brand] = filePath;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  ERROR: could not locate {label} file: {ex.Message}");
                    BrandsWithReadErrors.Add(brand);
                    continue;
                }

                Console.WriteLine($"Reading {label} payments from '{filePath}'...");

                List<Dictionary<string, string>> rows;
                try
                {
                    rows = ReadCsvRows(filePath);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  ERROR: could not read '{filePath}': {ex.Message}");
                    BrandsWithReadErrors.Add(brand);
                    continue;
                }

                foreach (var row in rows)
                {
                    if (!row.TryGetValue("UserID", out var uuid) || string.IsNullOrWhiteSpace(uuid))
                        continue;
                    uuid = uuid.Trim();

                    if (!accountsByUuid.ContainsKey(uuid))
                    {
                        pastelCodesByUuid.TryGetValue(uuid, out var pastelCode);
                        if (pastelCode == null)
                            Console.WriteLine($"  WARNING: no Pastel account code found for UUID {uuid} — invoice Reference will be blank.");

                        accountsByUuid[uuid] = new Account
                        {
                            Fullname = row.TryGetValue("Company", out var c) ? c.Trim() : null,
                            UUID = uuid,
                            Brand = row.TryGetValue("Brand", out var b) && !string.IsNullOrWhiteSpace(b) ? b.Trim() : brand,
                            TaxRef = row.TryGetValue("TaxRef", out var t) ? t.Trim() : null,
                            PastelAccount = pastelCode
                        };
                    }

                    var invoice = ParseInvoiceRow(row);
                    if (invoice == null)
                        continue;

                    Invoices.Add(invoice);
                    InvoiceSourceBrand[invoice.Inv] = brand;
                }

                Console.WriteLine($"  {rows.Count} rows read from {label} file.");
            }

            Accounts = accountsByUuid.Values.ToList();
            Console.WriteLine($"Derived {Accounts.Count} unique Accounts and {Invoices.Count} invoice lines from JHB1/JHB2 payment files.");
        }

        /// <summary>
        /// Reads G2CAccounts.csv (Full Name, Pastel Account Code, UUID) from
        /// AppPaths.Input into a UUID -> Pastel Account Code lookup. Not
        /// every payment-file UUID is guaranteed to have an entry here —
        /// missing ones are logged as warnings by the caller, not treated
        /// as fatal.
        /// </summary>
        private static Dictionary<string, string> ReadPastelAccountCodes()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var filePath = Path.Combine(AppPaths.Input, PastelAccountsFileName);

            if (!File.Exists(filePath))
            {
                Console.WriteLine($"WARNING: '{filePath}' not found — no Pastel account codes will be attached to invoices.");
                return result;
            }

            Console.WriteLine($"Reading Pastel account codes from '{filePath}'...");

            var rows = ReadCsvRows(filePath);
            foreach (var row in rows)
            {
                if (!row.TryGetValue("UUID", out var uuid) || string.IsNullOrWhiteSpace(uuid))
                    continue;
                if (!row.TryGetValue("PastelAccountCode", out var code) || string.IsNullOrWhiteSpace(code))
                    continue;

                result[uuid.Trim()] = code.Trim();
            }

            Console.WriteLine($"  {result.Count} Pastel account codes loaded.");
            return result;
        }

        private static string FindBrandFile(string[] patterns)
        {
            var allMatches = new List<string>();

            foreach (var pattern in patterns)
                allMatches.AddRange(Directory.GetFiles(AppPaths.Input, pattern));

            if (allMatches.Count == 0)
                throw new FileNotFoundException($"No file found in '{AppPaths.Input}' matching any of: {string.Join(", ", patterns)}");

            // Pick the most recently modified match — so last month's file
            // left in the folder doesn't get picked over this month's.
            var chosen = allMatches
                .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                .First();

            if (allMatches.Count > 1)
            {
                Console.WriteLine($"  Note: found {allMatches.Count} matching files, using the most recently modified: '{chosen}'");
                foreach (var f in allMatches.Where(f => f != chosen))
                    Console.WriteLine($"    (ignored older file: '{f}')");
            }

            return chosen;
        }

        /// <summary>
        /// Reads a CSV into a list of header-keyed rows. Header names are
        /// normalized the same way the original code did — spaces stripped,
        /// so "User ID" -> "UserID", "Tax Ref" -> "TaxRef", "Start time" ->
        /// "Starttime" — so lookups below match the real Go2Cloud export
        /// headers.
        /// </summary>
        private static List<Dictionary<string, string>> ReadCsvRows(string filePath)
        {
            var rows = new List<Dictionary<string, string>>();

            using (var reader = new CsvFileReader(filePath, EmptyLineBehavior.Ignore))
            {
                List<string> line;
                List<string> header = null;

                while (reader.ReadRow(line = new List<string>()))
                {
                    if (header == null)
                    {
                        header = line.Select(h => h.Trim().Replace(" ", "")).ToList();
                        continue;
                    }

                    if (line.All(field => string.IsNullOrWhiteSpace(field)))
                        continue;

                    var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < header.Count && i < line.Count; i++)
                        row[header[i]] = line[i];

                    rows.Add(row);
                }
            }

            return rows;
        }

        private static readonly string[] DateFormats =
        {
            "yyyy/MM/dd HH:mm",
            "yyyy/MM/dd HH:mm:ss",
            "yyyy-MM-dd HH:mm",
            "yyyy-MM-dd HH:mm:ss",
            "dd/MM/yyyy HH:mm",
            "dd/MM/yyyy HH:mm:ss",
            "dd-MM-yyyy HH:mm",
            "dd-MM-yyyy HH:mm:ss"
        };

        private static InvoiceShort ParseInvoiceRow(Dictionary<string, string> row)
        {
            DateTime.TryParseExact(
                row.TryGetValue("Starttime", out var st) ? st.Trim() : string.Empty,
                DateFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTime starttime);

            return new InvoiceShort
            {
                UserID = row.TryGetValue("UserID", out var u) ? u.Trim() : null,
                Inv = row.TryGetValue("Invoice", out var inv) ? inv.Trim() : null,
                Starttime = starttime,
                Value = row.TryGetValue("Value", out var v) ? v.Trim() : null,
                Total = row.TryGetValue("Total", out var tot) ? tot.Trim() : null
            };
        }

        // ---------- Writing: per-brand "Invoices JHB1/JHB2" output logs ----------

        private static void WriteInvoiceLogs(List<InvoicePostResult> results)
        {
            foreach (var (brand, label, _) in BrandFiles)
            {
                var brandResults = results
                    .Where(r => InvoiceSourceBrand.TryGetValue(r.InvoiceNumber ?? string.Empty, out var b)
                                && string.Equals(b, brand, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                WriteInvoiceLog(label, brandResults);
            }
        }

        private static void WriteInvoiceLog(string label, List<InvoicePostResult> results)
        {
            // Includes time, not just date - otherwise a second run on the
            // same day silently overwrites the first run's results file,
            // which is exactly what happened to earlier test runs.
            var path = Path.Combine(AppPaths.OutputInvoices, $"Invoices {label} {DateTime.Now:yyyy-MM-dd_HHmmss}.csv");

            var sb = new StringBuilder();
            sb.AppendLine("InvoiceNumber,DocumentNumber,CustomerName,Status,SageInvoiceId,Message");

            foreach (var r in results)
            {
                sb.AppendLine(string.Join(",",
                    CsvEscape(r.InvoiceNumber),
                    CsvEscape(r.DocumentNumber),
                    CsvEscape(r.CustomerName),
                    CsvEscape(r.Status),
                    CsvEscape(r.SageInvoiceId),
                    CsvEscape(r.Message)));
            }

            File.WriteAllText(path, sb.ToString());
            Console.WriteLine($"Wrote {path} ({results.Count} rows)");
        }

        private static string CsvEscape(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
                return "\"" + value.Replace("\"", "\"\"") + "\"";

            return value;
        }

        // ---------- Archiving: move each brand's source file to Processed or Error ----------
        // A file goes to Error if it failed to read at all, OR if any
        // invoice sourced from it ended up Skipped/Failed. Only a file
        // where every one of its invoices Posted successfully goes to
        // Processed. G2CAccounts.csv (the Pastel master list) is never
        // moved — it's a persistent reference file, not a monthly batch.

        private static void ArchiveSourceFiles(List<InvoicePostResult> results)
        {
            foreach (var (brand, label, _) in BrandFiles)
            {
                if (!BrandFilePaths.TryGetValue(brand, out var filePath))
                {
                    // Nothing was found for this brand this run — nothing to archive.
                    continue;
                }

                bool hasReadError = BrandsWithReadErrors.Contains(brand);

                var brandResults = results
                    .Where(r => InvoiceSourceBrand.TryGetValue(r.InvoiceNumber ?? string.Empty, out var b)
                                && string.Equals(b, brand, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                bool hasPostingError = brandResults.Any(r => !string.Equals(r.Status, "Posted", StringComparison.OrdinalIgnoreCase));

                var targetFolder = (hasReadError || hasPostingError) ? AppPaths.Error : AppPaths.Processed;
                MoveToFolder(filePath, targetFolder, label);
            }
        }

        private static void MoveToFolder(string filePath, string folderPath, string label)
        {
            try
            {
                Directory.CreateDirectory(folderPath);

                var fileName = Path.GetFileNameWithoutExtension(filePath);
                var extension = Path.GetExtension(filePath);
                // Timestamp suffix avoids overwriting a previous archive if
                // the same filename is ever reused across runs.
                var destName = $"{fileName}_{DateTime.Now:yyyyMMdd_HHmmss}{extension}";
                var destPath = Path.Combine(folderPath, destName);

                // File.Move (not File.Copy) - the source file is relocated
                // out of Input entirely, not duplicated. After this line,
                // filePath no longer exists; only destPath does.
                File.Move(filePath, destPath);
                Console.WriteLine($"Moved {label} source file to '{destPath}'");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WARNING: could not move {label} source file '{filePath}' to '{folderPath}': {ex.Message}");
            }
        }
    }
}