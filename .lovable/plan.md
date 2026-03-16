

# שיפור אלגוריתם OCR + לוגים + שדות חסרים

## סיכום
שיפור כולל של תהליך הסריקה ב-C# Backend: וולידציה לפי מטבע/תאריך, לוגים מפורטים לסריקות, ותיקון 4 שדות שלא ממופים כרגע (Expense Type, Total Amount, Extra Details).

---

## שינויים

### 1. וולידציה מטבע-תאריך (InvoiceService.cs)
פונקציה חדשה `ValidateDateByCurrency()` שרצה אחרי Scan 2:
- **PHP (פיליפינים)**: תאריך MM/DD/YYYY — אם המודל החזיר DD/MM/YYYY, מזהה ומתקן (אם היום > 12 זה סימן שהפורמט הפוך)
- **ILS (ישראל)**: תאריך DD/MM/YYYY — אם המודל החזיר MM/DD/YYYY, מתקן
- **USD (ארה"ב)**: MM/DD/YYYY — ולידציה שהחודש ≤ 12
- הוספת הנחיה מפורשת בפרומפט לפי מטבע

### 2. לוגים מפורטים לסריקות (InvoiceService.cs + ChatService.cs)
- טבלה חדשה `ocr_scan_logs` עם עמודות:
  - `id`, `user_id`, `session_id`, `scan1_raw`, `scan2_raw`, `final_fields` (JSON), `country`, `currency_detected`, `processing_time_ms`, `errors`, `created_at`
- שמירה אוטומטית בכל סריקה — Scan 1 raw, Scan 2 raw, תוצאה סופית, זמן עיבוד
- ישמש לדיבאג ולסאפורט

### 3. Expense Type — הוספת קטגוריות ומיפוי (InvoiceService.cs)
כרגע `Type` ממופה ל-`document_type` (sales_invoice/receipt) — לא ל-expense type.
- הוספת שדה `expense_type` לפרומפט עם הקטגוריות הספציפיות:
  `business_meal`, `vehicle`, `entertainment`, `hotel`, `internet`, `parking`, `other`, `meal`, `taxi`
- הוספת שדה `ExpenseType` ל-`InvoiceFields`
- מיפוי ב-`MapToAlgoTextFields()`

### 4. Total Amount — תיקון מיפוי (InvoiceService.cs)
כרגע `Total` מחושב מ-`payment.amount_paid` או מסכום amounts — אבל לא נשמר ב-`TotalAmount` כשדה נפרד.
- הוספת fallback: אם `amount_paid` ריק, חישוב vatable + tax = total
- הוספת שדה `TotalAmount` ל-`InvoiceFields` (בנוסף ל-`Total` הקיים) כדי לוודא שהערך תמיד מגיע

### 5. Extra Details — שמירת כל הנתונים הנסרקים (InvoiceFields + MapToAlgoTextFields)
הוספת שדה `ExtraDetails` (JSON string) ל-`InvoiceFields` שמכיל את **כל** ה-JSON הגולמי שהמודל החזיר — כולל שדות שלא ממופים לשדות ספציפיים:
- line items, כתובת לקוח, הערות, תנאי תשלום
- כל שדה נוסף שהמודל מחלץ מהחשבונית

---

## פרטים טכניים

| פריט | פירוט |
|---|---|
| קבצים שישתנו | `InvoiceService.cs`, `ApiModels.cs` (InvoiceFields), `TripExDbContext.cs`, `ChatService.cs` |
| Entity חדש | `OcrScanLog` |
| טבלה חדשה (migration) | `ocr_scan_logs` |
| שדות חדשים ב-InvoiceFields | `ExpenseType`, `TotalAmount`, `ExtraDetails` |
| פונקציות חדשות | `ValidateDateByCurrency()`, `LogOcrScan()` |
| פונקציות שישתנו | `GetExtractionPrompt()`, `MapToAlgoTextFields()`, `AnalyzeAsync()` |
| תלויות חדשות | אין |

### שינוי בפרומפט
הוספה ל-`GetExtractionPrompt()`:
```text
EXPENSE CATEGORY: Classify as one of: "business_meal", "vehicle", "entertainment", 
"hotel", "internet", "parking", "other", "meal", "taxi"

DATE FORMAT RULES:
- For PHP/Philippines: dates are MM/DD/YYYY
- For ILS/Israel: dates are DD/MM/YYYY  
- For USD/US: dates are MM/DD/YYYY
- Always output as YYYY-MM-DD
```

הוספה ל-JSON output format:
```text
"expense_type": "string",
"extra_details": { ...all other extracted fields... }
```

### סקריפט T-SQL לטבלה חדשה
```sql
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'ocr_scan_logs')
CREATE TABLE ocr_scan_logs (
    id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    user_id UNIQUEIDENTIFIER NULL,
    session_id UNIQUEIDENTIFIER NULL,
    scan1_raw NVARCHAR(MAX) NULL,
    scan2_raw NVARCHAR(MAX) NULL,
    final_fields NVARCHAR(MAX) NULL,
    country NVARCHAR(10) NULL,
    currency_detected NVARCHAR(10) NULL,
    processing_time_ms INT NULL,
    errors NVARCHAR(MAX) NULL,
    created_at DATETIME2 DEFAULT GETUTCDATE()
);
```

