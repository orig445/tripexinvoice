
# Sharpen Invoice Scanner (Keep Oracle)

## What We'll Do
Keep the same Oracle Llama 4 Maverick model but make two improvements:

1. **Sharpen the prompt** - Make the extraction instructions much more explicit with step-by-step logic so the model doesn't confuse VAT with subtotal
2. **Add math validation** - After the AI returns results, automatically verify and fix the numbers using simple arithmetic

## Changes

### File: `supabase/functions/analyze-invoice/index.ts`

### 1. Improved Prompt
- Add a clear step-by-step process the model must follow:
  - Step 1: Find the LARGEST amount on the document - that is likely the total
  - Step 2: Find the SMALLEST tax-related amount - that is VAT/tax
  - Step 3: The remaining middle amount is the subtotal
- Add explicit rule: **VAT/tax is ALWAYS smaller than subtotal**
- Add explicit rule: **subtotal + tax = total** (approximately)
- Add negative examples: "Do NOT put the subtotal value in tax_amount" and "Do NOT put the tax value in subtotal"

### 2. Post-Processing Math Validation
After parsing the AI response, add validation logic:

```text
IF total, subtotal, and tax all exist:
    IF tax > subtotal --> they are swapped, fix it
    IF subtotal + tax != total (within 5% tolerance):
        Recalculate tax = total - subtotal
IF only total and one other value exist:
    Calculate the missing value
IF only total exists:
    Leave subtotal and tax as null
```

This catches the most common error (VAT and subtotal being swapped) and auto-corrects it without needing to re-call the model.

---

## Technical Details

Single file change: `supabase/functions/analyze-invoice/index.ts`

- Enhanced `systemPrompt` with stricter extraction rules and negative examples
- New `validateAndFixAmounts()` function that runs after JSON parsing
- No new dependencies or API keys needed
- Edge function will be redeployed automatically
