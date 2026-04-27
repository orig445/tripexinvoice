using Microsoft.EntityFrameworkCore;
using TripEx.Api.Data;
using Xunit;
using System.Net;

namespace TripEx.Tests;

/// <summary>
/// Full-pipeline integration tests for InvoiceService.AnalyzeAsync.
/// Each test controls the fake AI response and asserts on the returned InvoiceFields.
/// </summary>
public class OcrIntegrationTests
{
    private static readonly string _img = TestHelpers.FakeImageBase64;

    // ── Happy path ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Analyze_StandardReceipt_AllFieldsCorrect()
    {
        var (svc, _) = TestHelpers.SuccessStack(TestHelpers.StandardReceipt);
        var result = await svc.AnalyzeAsync(_img, null, "IL");

        Assert.True(result.Success);
        Assert.NotNull(result.Fields);
        Assert.Equal("117.00", result.Fields!.Total);
        Assert.Equal("17.00", result.Fields.TotalVAT);
        Assert.Equal("ILS", result.Fields.Currency);
        Assert.Equal("2024-01-15", result.Fields.InvoiceDate);
        Assert.Equal("Test Cafe", result.Fields.MerchantName);
        Assert.Equal("RCP-001", result.Fields.InvoiceNumber);
        Assert.Equal("business_meal", result.Fields.ExpenseType);
        Assert.Equal("cash", result.Fields.FormOfPayment);
    }

    // ── Amount validation ─────────────────────────────────────────────────────

    [Fact]
    public async Task Analyze_TaxGreaterThanSubtotal_SwapsThem()
    {
        // AI returns tax=200, subtotal=50 — they are swapped
        var receipt = """
            {
              "document_type": "receipt", "invoice_date": null,
              "currency": "ILS", "expense_type": "other",
              "merchant": {"name": "Shop"},
              "amounts": {"vatable_sales_amount": 50.00, "non_vatable_sales_amount": 0, "service_charge_amount": 0, "tax_amount": 200.00},
              "payment": {"method": "cash", "amount_paid": 250.00, "form_of_payment": "cash", "card_last4": null, "card_type": null},
              "item_count": 1
            }
            """;
        var (svc, _) = TestHelpers.SuccessStack(receipt);
        var result = await svc.AnalyzeAsync(_img, null, "IL");

        Assert.True(result.Success);
        // After swap: tax=50, subtotal=200
        Assert.Equal("50.00", result.Fields!.TotalVAT);
        Assert.Equal("200.00", result.Fields.SubCategory);
    }

    [Fact]
    public async Task Analyze_TotalMismatch_TaxRecalculated()
    {
        // subtotal=90, tax=10 → sum=100, but paid=117 → correctedTax = 117-90 = 27
        var receipt = """
            {
              "document_type": "receipt", "invoice_date": null,
              "currency": "ILS", "expense_type": "other",
              "merchant": {"name": "Shop"},
              "amounts": {"vatable_sales_amount": 90.00, "non_vatable_sales_amount": 0, "service_charge_amount": 0, "tax_amount": 10.00},
              "payment": {"method": "cash", "amount_paid": 117.00, "form_of_payment": "cash", "card_last4": null, "card_type": null},
              "item_count": 1
            }
            """;
        var (svc, _) = TestHelpers.SuccessStack(receipt);
        var result = await svc.AnalyzeAsync(_img, null, "IL");

        Assert.True(result.Success);
        Assert.Equal("27.00", result.Fields!.TotalVAT);
        Assert.Equal("117.00", result.Fields.Total);
    }

    [Fact]
    public async Task Analyze_NegativeCorrectedTax_OriginalKept()
    {
        // subtotal=120, tax=10, paid=100 → correctedTax = -20 → guard fires, keep original
        var receipt = """
            {
              "document_type": "receipt", "invoice_date": null,
              "currency": "ILS", "expense_type": "other",
              "merchant": {"name": "Shop"},
              "amounts": {"vatable_sales_amount": 120.00, "non_vatable_sales_amount": 0, "service_charge_amount": 0, "tax_amount": 10.00},
              "payment": {"method": "cash", "amount_paid": 100.00, "form_of_payment": "cash", "card_last4": null, "card_type": null},
              "item_count": 1
            }
            """;
        var (svc, _) = TestHelpers.SuccessStack(receipt);
        var result = await svc.AnalyzeAsync(_img, null, "IL");

        Assert.True(result.Success);
        Assert.Equal("10.00", result.Fields!.TotalVAT); // unchanged
    }

    [Fact]
    public async Task Analyze_ZeroTax_NotTreatedAsSwap()
    {
        // tax=0, subtotal=100 — tax(0) is NOT > subtotal → no swap
        var receipt = """
            {
              "document_type": "receipt", "invoice_date": null,
              "currency": "ILS", "expense_type": "other",
              "merchant": {"name": "Shop"},
              "amounts": {"vatable_sales_amount": 100.00, "non_vatable_sales_amount": 0, "service_charge_amount": 0, "tax_amount": 0},
              "payment": {"method": "cash", "amount_paid": 100.00, "form_of_payment": "cash", "card_last4": null, "card_type": null},
              "item_count": 1
            }
            """;
        var (svc, _) = TestHelpers.SuccessStack(receipt);
        var result = await svc.AnalyzeAsync(_img, null, "IL");

        Assert.True(result.Success);
        Assert.Equal("0.00", result.Fields!.TotalVAT);
        Assert.Equal("100.00", result.Fields.SubCategory);
    }

    [Fact]
    public async Task Analyze_AmountsAsStringsWithCurrencySymbol_ParsedCorrectly()
    {
        // AI sometimes returns amounts as strings with ₪ prefix
        var receipt = """
            {
              "document_type": "receipt", "invoice_date": null,
              "currency": "ILS", "expense_type": "other",
              "merchant": {"name": "Shop"},
              "amounts": {"vatable_sales_amount": "₪100.00", "non_vatable_sales_amount": 0, "service_charge_amount": 0, "tax_amount": "₪17.00"},
              "payment": {"method": "cash", "amount_paid": "₪117.00", "form_of_payment": "cash", "card_last4": null, "card_type": null},
              "item_count": 1
            }
            """;
        var (svc, _) = TestHelpers.SuccessStack(receipt);
        var result = await svc.AnalyzeAsync(_img, null, "IL");

        Assert.True(result.Success);
        Assert.Equal("17.00", result.Fields!.TotalVAT);
        Assert.Equal("117.00", result.Fields.Total);
    }

    // ── Date normalisation ────────────────────────────────────────────────────

    [Fact]
    public async Task Analyze_DateDDMMYYYY_NormalizedToISO()
    {
        var receipt = TestHelpers.StandardReceipt.Replace("\"2024-01-15\"", "\"15/01/2024\"");
        var (svc, _) = TestHelpers.SuccessStack(receipt);
        var result = await svc.AnalyzeAsync(_img, null, "IL");

        Assert.True(result.Success);
        Assert.Equal("2024-01-15", result.Fields!.InvoiceDate);
    }

    [Fact]
    public async Task Analyze_DateAlreadyISO_Unchanged()
    {
        var (svc, _) = TestHelpers.SuccessStack(TestHelpers.StandardReceipt);
        var result = await svc.AnalyzeAsync(_img, null, "IL");

        Assert.True(result.Success);
        Assert.Equal("2024-01-15", result.Fields!.InvoiceDate);
    }

    [Fact]
    public async Task Analyze_DateShortYear_ExpandedToFourDigits()
    {
        var receipt = TestHelpers.StandardReceipt.Replace("\"2024-01-15\"", "\"15/01/24\"");
        var (svc, _) = TestHelpers.SuccessStack(receipt);
        var result = await svc.AnalyzeAsync(_img, null, "IL");

        Assert.True(result.Success);
        Assert.Equal("2024-01-15", result.Fields!.InvoiceDate);
    }

    [Fact]
    public async Task Analyze_AmbiguousDateWithILS_UsesDayFirst()
    {
        // "05/03/2024" — both ≤12, ambiguous. ILS → DD/MM → March 5
        var receipt = TestHelpers.StandardReceipt.Replace("\"2024-01-15\"", "\"05/03/2024\"");
        var (svc, _) = TestHelpers.SuccessStack(receipt); // currency in JSON is ILS
        var result = await svc.AnalyzeAsync(_img, null, "IL");

        Assert.True(result.Success);
        Assert.Equal("2024-03-05", result.Fields!.InvoiceDate);
    }

    [Fact]
    public async Task Analyze_AmbiguousDateWithUSD_UsesMonthFirst()
    {
        // "05/03/2024" — USD/US → MM/DD → May 3
        var receipt = TestHelpers.StandardReceipt
            .Replace("\"2024-01-15\"", "\"05/03/2024\"")
            .Replace("\"ILS\"", "\"USD\"");
        var (svc, _) = TestHelpers.SuccessStack(receipt);
        var result = await svc.AnalyzeAsync(_img, null, "US");

        Assert.True(result.Success);
        Assert.Equal("2024-05-03", result.Fields!.InvoiceDate);
    }

    [Fact]
    public async Task Analyze_NullDate_ReturnsNullInvoiceDate()
    {
        var receipt = TestHelpers.StandardReceipt.Replace("\"2024-01-15\"", "null");
        var (svc, _) = TestHelpers.SuccessStack(receipt);
        var result = await svc.AnalyzeAsync(_img, null, "IL");

        Assert.True(result.Success);
        Assert.Null(result.Fields!.InvoiceDate);
    }

    // ── Payment detection ─────────────────────────────────────────────────────

    [Fact]
    public async Task Analyze_ExplicitFormOfPaymentCredit_ReturnsCredit()
    {
        var receipt = TestHelpers.StandardReceipt
            .Replace("\"form_of_payment\": \"cash\"", "\"form_of_payment\": \"credit\"");
        var (svc, _) = TestHelpers.SuccessStack(receipt);
        var result = await svc.AnalyzeAsync(_img, null, "IL");

        Assert.True(result.Success);
        Assert.Equal("credit", result.Fields!.FormOfPayment);
    }

    [Fact]
    public async Task Analyze_MethodContainsAshrai_InferredCredit()
    {
        // form_of_payment is invalid → inference from method kicks in
        var receipt = TestHelpers.StandardReceipt
            .Replace("\"form_of_payment\": \"cash\"", "\"form_of_payment\": \"unknown\"")
            .Replace("\"method\": \"cash\"", "\"method\": \"אשראי VISA\"");
        var (svc, _) = TestHelpers.SuccessStack(receipt);
        var result = await svc.AnalyzeAsync(_img, null, "IL");

        Assert.True(result.Success);
        Assert.Equal("credit", result.Fields!.FormOfPayment);
    }

    [Fact]
    public async Task Analyze_MethodContainsBankTransfer_InferredBank()
    {
        var receipt = TestHelpers.StandardReceipt
            .Replace("\"form_of_payment\": \"cash\"", "\"form_of_payment\": \"unknown\"")
            .Replace("\"method\": \"cash\"", "\"method\": \"bank transfer\"");
        var (svc, _) = TestHelpers.SuccessStack(receipt);
        var result = await svc.AnalyzeAsync(_img, null, "IL");

        Assert.True(result.Success);
        Assert.Equal("bank", result.Fields!.FormOfPayment);
    }

    [Fact]
    public async Task Analyze_MethodContainsHaavarah_InferredBank()
    {
        var receipt = TestHelpers.StandardReceipt
            .Replace("\"form_of_payment\": \"cash\"", "\"form_of_payment\": \"unknown\"")
            .Replace("\"method\": \"cash\"", "\"method\": \"העברה בנקאית\"");
        var (svc, _) = TestHelpers.SuccessStack(receipt);
        var result = await svc.AnalyzeAsync(_img, null, "IL");

        Assert.True(result.Success);
        Assert.Equal("bank", result.Fields!.FormOfPayment);
    }

    [Fact]
    public async Task Analyze_NoPaymentSection_DefaultsCash()
    {
        var receipt = """
            {
              "document_type": "receipt", "invoice_date": "2024-01-15",
              "currency": "ILS", "expense_type": "other",
              "merchant": {"name": "Shop"},
              "amounts": {"vatable_sales_amount": 100.00, "non_vatable_sales_amount": 0, "service_charge_amount": 0, "tax_amount": 17.00},
              "item_count": 1
            }
            """;
        var (svc, _) = TestHelpers.SuccessStack(receipt);
        var result = await svc.AnalyzeAsync(_img, null, "IL");

        Assert.True(result.Success);
        Assert.Equal("cash", result.Fields!.FormOfPayment);
        // Total should be computed from amounts section
        Assert.Equal("117.00", result.Fields.Total);
    }

    [Fact]
    public async Task Analyze_CreditCardNumberAtEndOfMethod_ExtractsLast4()
    {
        var receipt = TestHelpers.StandardReceipt
            .Replace("\"form_of_payment\": \"cash\"", "\"form_of_payment\": \"credit\"")
            .Replace("\"method\": \"cash\"", "\"method\": \"VISA 1234\"")
            .Replace("\"card_last4\": null", "\"card_last4\": null"); // keep null to force regex
        var (svc, _) = TestHelpers.SuccessStack(receipt);
        var result = await svc.AnalyzeAsync(_img, null, "IL");

        Assert.True(result.Success);
        Assert.Equal("credit", result.Fields!.FormOfPayment);
        Assert.Equal("1234", result.Fields.CardLast4);
    }

    [Fact]
    public async Task Analyze_CreditCardStarMask_ExtractsLast4()
    {
        var receipt = TestHelpers.StandardReceipt
            .Replace("\"form_of_payment\": \"cash\"", "\"form_of_payment\": \"credit\"")
            .Replace("\"method\": \"cash\"", "\"method\": \"Mastercard ****-5678\"");
        var (svc, _) = TestHelpers.SuccessStack(receipt);
        var result = await svc.AnalyzeAsync(_img, null, "IL");

        Assert.True(result.Success);
        Assert.Equal("5678", result.Fields!.CardLast4);
    }

    // ── Expense type mapping ──────────────────────────────────────────────────

    [Theory]
    [InlineData("business_meal")]
    [InlineData("vehicle")]
    [InlineData("entertainment")]
    [InlineData("hotel")]
    [InlineData("internet")]
    [InlineData("parking")]
    [InlineData("other")]
    [InlineData("meal")]
    [InlineData("taxi")]
    public async Task Analyze_ValidExpenseType_MappedThrough(string expenseType)
    {
        var receipt = TestHelpers.StandardReceipt
            .Replace("\"business_meal\"", $"\"{expenseType}\"");
        var (svc, _) = TestHelpers.SuccessStack(receipt);
        var result = await svc.AnalyzeAsync(_img, null, "IL");

        Assert.True(result.Success);
        Assert.Equal(expenseType, result.Fields!.ExpenseType);
    }

    [Fact]
    public async Task Analyze_UnknownExpenseType_MapsToOther()
    {
        var receipt = TestHelpers.StandardReceipt
            .Replace("\"business_meal\"", "\"restaurant\"");
        var (svc, _) = TestHelpers.SuccessStack(receipt);
        var result = await svc.AnalyzeAsync(_img, null, "IL");

        Assert.True(result.Success);
        Assert.Equal("other", result.Fields!.ExpenseType);
    }

    // ── Merchant name cleaning ────────────────────────────────────────────────

    [Fact]
    public async Task Analyze_MerchantNameWithNewlines_Cleaned()
    {
        // JSON \n inside a string value → actual newline after parse → cleaned to space
        var receipt = TestHelpers.StandardReceipt
            .Replace("\"Test Cafe\"", "\"Test\\nCafe\"");
        var (svc, _) = TestHelpers.SuccessStack(receipt);
        var result = await svc.AnalyzeAsync(_img, null, "IL");

        Assert.True(result.Success);
        Assert.Equal("Test Cafe", result.Fields!.MerchantName);
    }

    // ── Edge cases for amounts ────────────────────────────────────────────────

    [Fact]
    public async Task Analyze_NoAmountsSection_TotalFromTopLevel()
    {
        // No amounts section, but top-level "total" field
        var receipt = """
            {
              "document_type": "receipt", "invoice_date": "2024-01-15",
              "currency": "PHP", "expense_type": "other",
              "total": 500.00,
              "merchant": {"name": "Shop"},
              "payment": {"method": "cash", "amount_paid": 500.00, "form_of_payment": "cash", "card_last4": null, "card_type": null},
              "item_count": 1
            }
            """;
        var (svc, _) = TestHelpers.SuccessStack(receipt);
        var result = await svc.AnalyzeAsync(_img, null, "PH");

        Assert.True(result.Success);
        Assert.Equal("500.00", result.Fields!.Total);
    }

    [Fact]
    public async Task Analyze_AllNullFields_ReturnsSuccessWithNulls()
    {
        // AI returns bare minimum — should not throw
        var receipt = """{"document_type": "receipt"}""";
        var (svc, _) = TestHelpers.SuccessStack(receipt);
        var result = await svc.AnalyzeAsync(_img, null, "IL");

        Assert.True(result.Success);
        Assert.NotNull(result.Fields);
        Assert.Null(result.Fields!.Total);
        Assert.Null(result.Fields.Currency);
    }

    // ── Logging ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Analyze_SuccessfulScan_LogStatusIsSuccess()
    {
        var (svc, db) = TestHelpers.SuccessStack(TestHelpers.StandardReceipt);
        await svc.AnalyzeAsync(_img, null, "IL", Guid.NewGuid());

        var log = await db.InvoiceScanLogs.FirstAsync();
        Assert.Equal("Success", log.Status);
    }

    [Fact]
    public async Task Analyze_NullUserId_LogSavedWithEmptyGuid()
    {
        var (svc, db) = TestHelpers.SuccessStack(TestHelpers.StandardReceipt);
        await svc.AnalyzeAsync(_img, null, "IL", userId: null);

        var log = await db.InvoiceScanLogs.FirstAsync();
        Assert.Equal(Guid.Empty, log.UserId);
    }

    [Fact]
    public async Task Analyze_FailedScan_LogStatusIsFailed()
    {
        var (svc, db) = TestHelpers.ErrorStack(HttpStatusCode.InternalServerError);
        await svc.AnalyzeAsync(_img, null, "IL", Guid.NewGuid());

        var log = await db.InvoiceScanLogs.FirstAsync();
        Assert.Equal("Failed", log.Status);
    }

    // ── HTTP error handling ───────────────────────────────────────────────────

    [Fact]
    public async Task Analyze_Http500_ReturnsFailure()
    {
        var (svc, _) = TestHelpers.ErrorStack(HttpStatusCode.InternalServerError);
        var result = await svc.AnalyzeAsync(_img, null, "IL");

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Analyze_Http429RateLimit_ReturnsFailure()
    {
        var (svc, _) = TestHelpers.ErrorStack(HttpStatusCode.TooManyRequests, "Rate limit exceeded");
        var result = await svc.AnalyzeAsync(_img, null, "IL");

        Assert.False(result.Success);
        Assert.Contains("429", result.Error);
    }

    [Fact]
    public async Task Analyze_Http401Unauthorized_ReturnsFailure()
    {
        var (svc, _) = TestHelpers.ErrorStack(HttpStatusCode.Unauthorized, "Invalid API key");
        var result = await svc.AnalyzeAsync(_img, null, "IL");

        Assert.False(result.Success);
    }

    // ── Input validation ──────────────────────────────────────────────────────

    [Fact]
    public async Task Analyze_EmptyImageBase64_ImmediateFailure()
    {
        var (svc, _) = TestHelpers.SuccessStack(TestHelpers.StandardReceipt);
        var result = await svc.AnalyzeAsync("", null, "IL");

        Assert.False(result.Success);
        Assert.Contains("imageBase64 or imageUrl", result.Error);
    }

    [Fact]
    public async Task Analyze_NullImageBase64AndUrl_ImmediateFailure()
    {
        var (svc, _) = TestHelpers.SuccessStack(TestHelpers.StandardReceipt);
        var result = await svc.AnalyzeAsync(null, null, "IL");

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Analyze_RawBase64WithoutPrefix_HandledCorrectly()
    {
        // Base64 without "data:image/..." prefix — service should wrap it
        var (svc, _) = TestHelpers.SuccessStack(TestHelpers.StandardReceipt);
        var result = await svc.AnalyzeAsync(TestHelpers.RawBase64, null, "IL");

        Assert.True(result.Success);
    }

    [Fact]
    public async Task Analyze_NullCountry_DefaultsGracefully()
    {
        var (svc, _) = TestHelpers.SuccessStack(TestHelpers.StandardReceipt);
        var result = await svc.AnalyzeAsync(_img, null, country: null);

        Assert.True(result.Success);
    }
}
