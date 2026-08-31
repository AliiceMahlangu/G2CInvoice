using G2C.Invoice.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace G2C.Invoice
{
    // Targets the Sage Business Cloud Accounting "2.0.0" reseller API
    // (Company/Get, Customer/Get, Account/Get, TaxType/Get,
    // TaxInvoice/Save). Confirmed working against the live sandbox.
    //
    // Every request goes through SageAuth.BuildRequestPath(), which appends
    // the required "apikey" query string parameter (never a header — the API
    // returns 403 if you put it in headers) and any extra params like
    // CompanyId.
    internal class SageClient
    {
        private readonly HttpClient _http;

        // FAQ 6.4 (429 Request Limit Reached): the API allows up to 100
        // requests/minute/company (~1.67/sec) and recommends staying at or
        // below ~1 request/sec. Conservative pause between calls in any loop
        // that hits the API once per record.
        private const int RequestDelayMs = 700;

        // ---------- Ledger account for invoice lines ----------
        // Confirmed against live Account/Get response: the chart of accounts
        // is generic (Advertising, Bank Charges, Salaries & Wages, etc.) with
        // no per-brand split. Every invoice line posts to this single income
        // account regardless of brand.
        private const string DefaultLedgerAccountName = "Other Sales";

        // ---------- Tax type name matching ----------
        // TODO: confirm these substrings actually appear in the "Name"
        // field TaxType/Get returns for your company. These are guesses
        // based on standard SA VAT naming conventions, not confirmed data.
        // Confirmed against live TaxType/Get response: real names are
        // "Zero Rate" (ID=150191) and "Standard Rate" (ID=150189).
        private const string ZeroRatedTaxTypeNameContains = "Zero Rate";
        private const string StandardRateTaxTypeNameContains = "Standard Rate";

        public SageClient()
        {
            _http = SageAuth.CreateClient();
        }

        // ---------- low-level helpers ----------

        private async Task<string> GetAsync(string endpoint, params (string key, string value)[] extraQueryParams)
        {
            var path = SageAuth.BuildRequestPath(endpoint, extraQueryParams);
            var response = await _http.GetAsync(path);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"GET {endpoint} failed: {(int)response.StatusCode} {response.StatusCode} - {body}");
            return body;
        }

        private async Task<string> PostAsync(string endpoint, object payload, params (string key, string value)[] extraQueryParams)
        {
            var path = SageAuth.BuildRequestPath(endpoint, extraQueryParams);
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _http.PostAsync(path, content);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"POST {endpoint} failed: {(int)response.StatusCode} {response.StatusCode} - {body}");
            return body;
        }

        /// <summary>
        /// The documented responses (Company/Get, TaxInvoice/Save) share a
        /// "TotalResults" / "ReturnedResults" / "Results": [...] envelope.
        /// This pulls the "Results" array when present, or wraps a single
        /// object in a one-item list otherwise.
        /// </summary>
        private static List<JsonElement> ExtractResults(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement.Clone();

            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("Results", out var results))
                return results.EnumerateArray().Select(e => e.Clone()).ToList();

            if (root.ValueKind == JsonValueKind.Array)
                return root.EnumerateArray().Select(e => e.Clone()).ToList();

            return new List<JsonElement> { root };
        }

        private static (string Id, string Name) ReadIdAndName(JsonElement item, string nameField = "Name")
        {
            var id = item.TryGetProperty("ID", out var idProp)
                ? idProp.ToString()
                : null;
            var name = item.TryGetProperty(nameField, out var nameProp)
                ? nameProp.GetString()
                : null;
            return (id, name);
        }

        private static string GetField(JsonElement element, string fieldName)
        {
            if (!element.TryGetProperty(fieldName, out var prop))
                return null;

            return prop.ValueKind switch
            {
                JsonValueKind.String => prop.GetString(),
                JsonValueKind.Number => prop.ToString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null
            };
        }

        // ---------- 1. Confirm / resolve the CompanyId ----------

        public async Task ResolveCompanyIdAsync()
        {
            Console.WriteLine("Verifying Sage CompanyId...");

            var json = await GetAsync("Company/Get");
            var companies = ExtractResults(json)
                .Select(item => ReadIdAndName(item))
                .ToList();

            var currentId = SageAuth.CompanyId.ToString();
            if (companies.Any(c => c.Id == currentId))
            {
                Console.WriteLine($"Confirmed CompanyId {SageAuth.CompanyId} is accessible.");
                return;
            }

            Console.WriteLine($"WARNING: CompanyId {SageAuth.CompanyId} was not found in Company/Get results.");
            Console.WriteLine("Available companies:");
            foreach (var (id, name) in companies)
                Console.WriteLine($"  ID={id}  Name={name}");
        }

        // ---------- 2. Load ledger accounts, match by Brand ----------

        public async Task LoadAccountsAsync(List<Account> accounts)
        {
            Console.WriteLine("Loading Sage accounts (Account/Get)...");

            var json = await GetAsync("Account/Get", ("CompanyId", SageAuth.CompanyId.ToString()));
            var sageAccounts = ExtractResults(json)
                .Select(item => ReadIdAndName(item))
                .ToList();

            Console.WriteLine($"Fetched {sageAccounts.Count} accounts from Sage:");
            foreach (var (id, name) in sageAccounts)
                Console.WriteLine($"  ID={id}  Name={name}");

            var defaultAccount = sageAccounts.FirstOrDefault(a =>
                string.Equals(a.Name, DefaultLedgerAccountName, StringComparison.OrdinalIgnoreCase));

            if (defaultAccount.Id == null)
            {
                Console.WriteLine($"  WARNING: no Account/Get match for '{DefaultLedgerAccountName}' — no invoices can be posted until this is fixed.");
                return;
            }

            foreach (var account in accounts)
                account.SageLedgerAccountId = defaultAccount.Id;
        }

        // ---------- 2b. Load items (diagnostic) ----------
        // Both LineType 0 and 1 with an AccountId as SelectionId failed
        // "Valid Selection Required". Testing whether SelectionId actually
        // needs an ItemId from Item/Get instead of an Account/Get ID.

        public List<(string Id, string Name)> Items { get; private set; }
            = new List<(string Id, string Name)>();

        public async Task LoadItemsAsync()
        {
            Console.WriteLine("Loading Sage items (Item/Get)...");

            var json = await GetAsync("Item/Get", ("CompanyId", SageAuth.CompanyId.ToString()));
            Items = ExtractResults(json)
                .Select(item => ReadIdAndName(item))
                .ToList();

            Console.WriteLine($"Fetched {Items.Count} items from Sage:");
            foreach (var (id, name) in Items)
                Console.WriteLine($"  ID={id}  Name={name}");
        }

        // ---------- 3. Load tax types ----------

        public List<(string Id, string Name)> TaxTypes { get; private set; }
            = new List<(string Id, string Name)>();

        public async Task LoadTaxTypesAsync()
        {
            Console.WriteLine("Loading Sage tax types (TaxType/Get)...");

            var json = await GetAsync("TaxType/Get", ("CompanyId", SageAuth.CompanyId.ToString()));
            TaxTypes = ExtractResults(json)
                .Select(item => ReadIdAndName(item))
                .ToList();

            Console.WriteLine($"Fetched {TaxTypes.Count} tax types from Sage:");
            foreach (var (id, name) in TaxTypes)
                Console.WriteLine($"  ID={id}  Name={name}");
        }

        /// <summary>
        /// Zero-rated when the account has a Tax Ref, standard (15%)
        /// otherwise. Returns (null, null) if no matching tax type name is
        /// found — callers must treat that as "cannot post this invoice",
        /// not fall back to a guess.
        /// </summary>
        private (string Id, string Name) ResolveTaxType(Account account)
        {
            bool zeroRated = !string.IsNullOrWhiteSpace(account.TaxRef);
            var nameContains = zeroRated ? ZeroRatedTaxTypeNameContains : StandardRateTaxTypeNameContains;

            return TaxTypes.FirstOrDefault(t =>
                t.Name != null && t.Name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        // ---------- 4. Resolve / create customers for each account ----------

        public async Task ResolveCustomersAsync(List<Account> accounts)
        {
            Console.WriteLine("Loading Sage customers (Customer/Get)...");

            var json = await GetAsync("Customer/Get", ("CompanyId", SageAuth.CompanyId.ToString()));
            var customers = ExtractResults(json)
                .Select(item => ReadIdAndName(item))
                .ToList();

            Console.WriteLine($"Fetched {customers.Count} customers from Sage.");

            foreach (var account in accounts)
            {
                var match = customers.FirstOrDefault(c =>
                    string.Equals(c.Name, account.Fullname, StringComparison.OrdinalIgnoreCase));

                if (match.Id != null)
                {
                    account.SageContactId = match.Id;
                    continue;
                }

                Console.WriteLine($"  No existing customer for '{account.Fullname}' — creating via Customer/Save.");

                // NOTE: Customer/Save's request schema isn't fully documented.
                // "Name" is the only field confirmed by the Customer/Get
                // sample. Confirm required/allowed fields (e.g. TaxReference)
                // against the real endpoint before relying on this for
                // anything beyond a bare-minimum customer record.
                var payload = new { Name = account.Fullname };

                var createJson = await PostAsync("Customer/Save", payload,
                    ("CompanyId", SageAuth.CompanyId.ToString()));

                var created = ReadIdAndName(ExtractResults(createJson).First());
                account.SageContactId = created.Id;

                await Task.Delay(RequestDelayMs);
            }
        }

        // ---------- 5. Post invoices ----------

        public async Task<List<InvoicePostResult>> PostInvoicesAsync(List<InvoiceShort> invoices, List<Account> accounts)
        {
            Console.WriteLine("Posting invoices to Sage (TaxInvoice/Save)...");

            if (TaxTypes.Count == 0)
                throw new InvalidOperationException("Call LoadTaxTypesAsync() before posting invoices.");

            var results = new List<InvoicePostResult>();
            int posted = 0, skipped = 0;

            foreach (var invoice in invoices)
            {
                var account = accounts.FirstOrDefault(a =>
                    string.Equals(a.UUID, invoice.UserID, StringComparison.OrdinalIgnoreCase));

                if (account == null || account.SageContactId == null || account.SageLedgerAccountId == null)
                {
                    var reason = $"No matched/resolved account for UserID {invoice.UserID}";
                    Console.WriteLine($"  SKIPPED invoice {invoice.Inv}: {reason}");
                    results.Add(new InvoicePostResult
                    {
                        InvoiceNumber = invoice.Inv,
                        CustomerName = account?.Fullname,
                        Status = "Skipped",
                        Message = reason
                    });
                    skipped++;
                    continue;
                }

                if (!decimal.TryParse(invoice.Total, System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out var totalAmount))
                {
                    var reason = $"Could not parse Total '{invoice.Total}'";
                    Console.WriteLine($"  SKIPPED invoice {invoice.Inv}: {reason}");
                    results.Add(new InvoicePostResult
                    {
                        InvoiceNumber = invoice.Inv,
                        CustomerName = account.Fullname,
                        Status = "Skipped",
                        Message = reason
                    });
                    skipped++;
                    continue;
                }

                var taxType = ResolveTaxType(account);
                if (taxType.Id == null)
                {
                    var reason = $"Could not resolve a tax type (TaxRef={(string.IsNullOrWhiteSpace(account.TaxRef) ? "<none>" : account.TaxRef)})";
                    Console.WriteLine($"  SKIPPED invoice {invoice.Inv}: {reason}");
                    results.Add(new InvoicePostResult
                    {
                        InvoiceNumber = invoice.Inv,
                        CustomerName = account.Fullname,
                        Status = "Skipped",
                        Message = reason
                    });
                    skipped++;
                    continue;
                }

                var invoiceDate = invoice.Starttime.ToString("yyyy-MM-dd");

                // Confirmed working against live TaxInvoice/Save: LineType 1
                // with an Account/Get ID as SelectionId posts successfully.
                // (LineType 0 with the same AccountId failed "Valid Selection
                // Required" — Item/Get confirmed 0 items exist for this
                // company, ruling out an Item-based SelectionId entirely.)
                const int AccountLineType = 1;

                // Assigned here (not earlier) so a number is only burned for
                // an invoice that actually reaches the API — the skip-checks
                // above don't consume a number. The original Go2Cloud
                // reference (invoice.Inv) stays traceable in the line
                // Description below, it isn't lost by moving off
                // DocumentNumber.
                var documentNumber = DocumentNumberSequence.Next();

                var payload = new
                {
                    CustomerId = account.SageContactId,
                    Date = invoiceDate,
                    DueDate = invoiceDate,
                    DocumentNumber = documentNumber,
                    Reference = account.PastelAccount ?? invoice.Inv,
                    Message = "",
                    Lines = new[]
                    {
                        new
                        {
                            SelectionId = account.SageLedgerAccountId,
                            TaxTypeId = taxType.Id,
                            Description = $"G2C Invoice {invoice.Inv}",
                            LineType = AccountLineType,
                            Quantity = 1,
                            UnitPriceExclusive = totalAmount,
                            Exclusive = totalAmount,
                            Total = totalAmount
                        }
                    }
                };

                try
                {
                    var responseJson = await PostAsync("TaxInvoice/Save", payload,
                        ("CompanyId", SageAuth.CompanyId.ToString()));

                    using var doc = JsonDocument.Parse(responseJson);
                    var root = doc.RootElement;

                    results.Add(new InvoicePostResult
                    {
                        InvoiceNumber = invoice.Inv,
                        CustomerName = account.Fullname,
                        Status = "Posted",
                        SageInvoiceId = GetField(root, "ID"),
                        DocumentNumber = documentNumber,
                        Message = GetField(root, "Status")
                    });
                    posted++;
                }
                catch (HttpRequestException ex)
                {
                    Console.WriteLine($"  FAILED invoice {invoice.Inv}: {ex.Message}");
                    results.Add(new InvoicePostResult
                    {
                        InvoiceNumber = invoice.Inv,
                        CustomerName = account.Fullname,
                        Status = "Failed",
                        DocumentNumber = documentNumber,
                        Message = ex.Message
                    });
                    skipped++;
                }

                await Task.Delay(RequestDelayMs);
            }

            Console.WriteLine($"Posted {posted} invoices, skipped {skipped}.");
            return results;
        }
    }
}