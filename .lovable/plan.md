

# תוכנית: אימון OCR מבוסס למידת דפוסים (Pattern Learning)

## הרעיון
במקום להזריק נתוני קבלות אמיתיים לפרומפט, המערכת תלמד **דפוסים מבניים** מהקבלות שמועלות — איפה מופיע התאריך, איפה הסכום, איפה שם הספק, וכו'. התובנות יתורגמו ל**כללים דינמיים** שנוספים לפרומפט של `OracleAiService`.

## איך זה עובד

```text
קבלות נסרקות → תוצאות נשמרות → המערכת מנתחת דפוסים → כללים חדשים נכתבים לפרומפט
```

**דוגמה:** אם ב-80% מהקבלות הישראליות התאריך מופיע בראש המסמך, ובטרמינלים של Maya התאריך מופיע בתחתית — הכלל הזה נכנס לפרומפט אוטומטית.

## שינויים טכניים

### 1. טבלת SQL Server חדשה — `OcrTrainingPatterns`
שומרת דפוסים שנלמדו (לא נתוני קבלות):
- `field_name` — איזה שדה (date, amount, vendor, payment_method...)
- `pattern_rule` — הכלל שנלמד ("Israeli receipts: date usually at top-right")
- `country` / `currency` — הקשר
- `confidence` — כמה פעמים הדפוס חזר על עצמו
- `source_count` — מכמה קבלות נלמד

### 2. טבלת SQL Server — `OcrTrainingSamples`
שומרת תוצאות סריקה מאומתות (בלי תמונות, רק metadata):
- `vendor_name`, `country`, `currency`, `document_type`
- `field_positions` (JSON) — מיפוי: איפה כל שדה נמצא ("date: top", "amount: bottom-right")
- `extraction_success` (bool) — האם החילוץ היה מדויק
- `corrections` (JSON) — מה תוקן ידנית

### 3. עדכון `OracleAiService.PrepareSystemPrompt`
- טעינת דפוסים רלוונטיים מ-`OcrTrainingPatterns` לפי country/currency
- הזרקה לפרומפט כסעיף "LEARNED PATTERNS":
  ```
  LEARNED PATTERNS (from {N} analyzed receipts):
  - DATE: In Israeli receipts, date is usually at top-right corner (85% confidence)
  - AMOUNT: Terminal receipts show total after "SALE AMOUNT" label (92%)
  - PAYMENT: "סליקת אשראי" always means credit card payment (100%)
  ```

### 4. Endpoint חדש — `POST /api/invoice/bulk-train`
- מקבל תמונה base64
- סורק עם `OracleAiService.CallGeminiFlashAsync`
- שומר תוצאה ב-`OcrTrainingSamples`
- מחזיר תוצאה ללקוח לאישור/דחייה

### 5. Endpoint — `POST /api/invoice/rebuild-patterns`
- עובר על כל ה-`OcrTrainingSamples` המאומתים
- מנתח דפוסים חוזרים (היכן שדות מופיעים, מה הפורמטים הנפוצים)
- כותב/מעדכן כללים ב-`OcrTrainingPatterns`
- פרומפט הבא שייטען ישתמש בכללים החדשים

### 6. עדכון `BulkReceiptTraining.tsx`
- קריאה ל-C# backend (`/api/invoice/bulk-train`) במקום Edge Function
- הסרת שמירה ל-knowledge_documents (לא רלוונטי)
- הוספת כפתורי ✅ אישור / ❌ דחייה לכל קבלה מעובדת
- הוספת כפתור "בנה דפוסים מחדש" שקורא ל-rebuild-patterns
- הצגת סטטיסטיקות: כמה דפוסים נלמדו, כמה קבלות מאומתות

### 7. עדכון `ApiModels.cs`
- מודלים חדשים: `OcrTrainingSample`, `OcrTrainingPattern`, `BulkTrainRequest/Response`

### 8. עדכון `TripExDbContext.cs` + `init-db.sql`
- הוספת Entity classes ו-SQL ליצירת הטבלאות

## קבצים שישתנו
- `dotnet-backend/TripEx.Api/Services/OracleAiService.cs` — פרומפט דינמי עם דפוסים
- `dotnet-backend/TripEx.Api/Services/InvoiceService.cs` — לוגיקת אימון ובניית דפוסים
- `dotnet-backend/TripEx.Api/Controllers/InvoiceController.cs` — endpoints חדשים
- `dotnet-backend/TripEx.Api/Models/ApiModels.cs` — מודלים חדשים
- `dotnet-backend/TripEx.Api/Data/TripExDbContext.cs` — טבלאות חדשות
- `dotnet-backend/TripEx.Api/Data/init-db.sql` — SQL ליצירת טבלאות
- `src/components/admin/BulkReceiptTraining.tsx` — חיבור ל-C# + אישור/דחייה
- `src/lib/api-service.ts` — endpoints חדשים

## תוצאה
ככל שמעלים יותר קבלות ומאשרים תוצאות ← המערכת מזהה דפוסים חוזרים ← הפרומפט מתעדכן עם כללים מדויקים יותר ← הדיוק עולה, בלי לחשוף מידע רגיש בין סריקות.

