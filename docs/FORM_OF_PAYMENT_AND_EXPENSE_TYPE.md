# Form of Payment & Expense Type — מסמך מוצרי טכני

> איך TripEX AI לוקח קבלה/חשבונית ומגדיר בוודאות מהו **אמצעי התשלום** (Form of Payment)
> ומהו **סוג ההוצאה** (Expense Type) — משלב ה-OCR ועד ה-ID הסופי שחוזר ללקוח.

---

## תוכן עניינים

1. [תמונה כללית](#1-תמונה-כללית)
2. [שני הפלטים ומה כל אחד מייצג](#2-שני-הפלטים-ומה-כל-אחד-מייצג)
3. [Form of Payment — הגדרה מלאה](#3-form-of-payment--הגדרה-מלאה)
   - [שלב א׳ — חילוץ ע״י ה-AI](#שלב-א׳--חילוץ-עי-ה-ai)
   - [שלב ב׳ — נורמליזציה שרתית (Inference)](#שלב-ב׳--נורמליזציה-שרתית-inference)
   - [שלב ג׳ — פרטי כרטיס אשראי](#שלב-ג׳--פרטי-כרטיס-אשראי)
   - [שלב ד׳ — מיפוי ל-ID של הלקוח](#שלב-ד׳--מיפוי-ל-id-של-הלקוח)
4. [Expense Type — הגדרה מלאה](#4-expense-type--הגדרה-מלאה)
   - [שלב א׳ — חילוץ ע״י ה-AI](#שלב-א׳--חילוץ-עי-ה-ai-1)
   - [שלב ב׳ — ולידציה ל-enum מותר](#שלב-ב׳--ולידציה-ל-enum-מותר)
   - [שלב ג׳ — מיפוי ל-ID של הלקוח](#שלב-ג׳--מיפוי-ל-id-של-הלקוח-1)
5. [מבנה הפלט (JSON) שחוזר ללקוח](#5-מבנה-הפלט-json-שחוזר-ללקוח)
6. [טבלת מקרי-קצה והתנהגות ברירת-מחדל](#6-טבלת-מקרי-קצה-והתנהגות-ברירת-מחדל)
7. [היכן זה יושב בקוד](#7-היכן-זה-יושב-בקוד)

---

## 1. תמונה כללית

לכל קבלה עוברים **שני ערכים מקבילים** בכל אחד משני השדות:

| ערך | מה זה | מקור |
|------|--------|------|
| **הקטגוריה הסמנטית** (`form_of_payment`, `expense_type`) | ערך קנוני מתוך רשימה סגורה של המערכת (`credit`/`cash`/`bank`, `business_meal`/`taxi`/…) | ה-AI + נורמליזציה שרתית |
| **ה-ID של הלקוח** (`form_of_payment_id`, `expense_type_id`) | המזהה המספרי מתוך **רשימת הערכים של מערכת הלקוח** (combtas / AlgoText) | מיפוי בין הקטגוריה לרשימה שהלקוח שלח בבקשה |

הרעיון המרכזי: ה-AI תמיד מחזיר קטגוריה **קנונית ויציבה**, ובנפרד המערכת ממפה אותה למזהה **הספציפי של הלקוח**. כך גם אם ללקוח יש שמות אמצעי-תשלום/סוגי-הוצאה משלו (למשל `"C.C - Employee"` או `"אשראי חברה"`), אנחנו יודעים בדיוק לאיזה ID לשייך אותם.

הצינור מיושם בשני מקומות זהים לוגית:

- **`supabase/functions/analyze-invoice/index.ts`** — Edge Function (Deno) — גרסה קלה, קטגוריה בלבד.
- **`dotnet-backend/TripEx.Api`** — ה-backend המלא (C#) — כולל גם מיפוי ל-ID של הלקוח.

הסעיפים הבאים מתארים את **הגרסה המלאה** (ה-.NET), ומציינים היכן ה-Edge Function מתנהג אחרת.

---

## 2. שני הפלטים ומה כל אחד מייצג

**Form of Payment:**
- קטגוריה קנונית: `"credit"` | `"cash"` | `"bank"` (שדה `form_of_payment`).
- ID לקוח: `form_of_payment_id` — מספר מתוך `FormOfPayments` שהלקוח שלח, או `null`.

**Expense Type:**
- קטגוריה קנונית: אחת מ-`business_meal | vehicle | entertainment | hotel | internet | parking | meal | taxi | other` (שדה `expense_type`).
- ID לקוח: `expense_type_id` — מספר מתוך `ExpenseTypes` שהלקוח שלח, או `null`.

הרשימות של הלקוח מגיעות בבקשת ה-`AnalyzeInvoiceRequest`:

```jsonc
{
  "imageBase64": "…",
  "country": "IL",
  "ExpenseTypes":    [ { "Id": 12, "Name": "Business Meal" }, { "Id": 30, "Name": "Taxi" } ],
  "FormOfPayments":  [ { "Id": 1,  "Name": "C.C - Employee" }, { "Id": 2, "Name": "Cash" } ]
}
```

> המערכת מקבלת גם את שמות ה-combtas/AlgoText: `ListOfExpenseType` / `ListOfFormOfPayment`,
> ולכל פריט גם `ExpenseTypeId`/`ExpenseTypeDesc` ו-`FormOfPaymentId`/`FormOfPaymentDesc`.
> ראו `ApiModels.cs:64–134`.

---

## 3. Form of Payment — הגדרה מלאה

ההגדרה נבנית ב-**ארבעה שלבים רצופים**, כל שלב הוא רשת-ביטחון לזה שלפניו.

### שלב א׳ — חילוץ ע״י ה-AI

ה-prompt מנחה את מודל ה-Vision (Gemini 2.5 Flash / Llama 4 דרך Oracle GenAI) להחזיר `payment.form_of_payment` כאחד מ-`credit|cash|bank`, לפי מילון רב-לשוני:

- **credit** — אם מופיע: `אשראי`, `כרטיס`, `סליקה`, `סליקת אשראי`, `credit`, `debit`, `visa`, `mastercard`, `amex`, `EMV`, `Contactless`, chip, swipe, או מספר כרטיס גלוי.
- **bank** — אם מופיע: `העברה`, `bank`, `transfer`, `wire`.
- **cash** — אם מופיע: `מזומן`, `cash`.
- **כלל זהב:** `"סליקת אשראי"` הוא **תמיד** credit ולעולם לא cash.
- אם לא נמצא אמצעי תשלום → ברירת-מחדל `cash`.

בנוסף, ה-AI מתבקש להחזיר את **הטקסט המקורי** של אמצעי התשלום ב-`payment.method` (מילה במילה), וזה מהווה חומר גלם לשלב הבא.

אם הלקוח שלח `FormOfPayments`, ה-prompt מוסיף בלוק ייעודי:

```
FORM OF PAYMENT OPTIONS (caller-supplied — pick the best matching ID for form_of_payment_id):
  1: "C.C - Employee"
  2: "Cash"
- Return the numeric ID of the best match as "form_of_payment_id".
- If none fit, return null for form_of_payment_id.
```

כך ה-AI מנסה כבר בשלב זה להחזיר את ה-`form_of_payment_id` הנכון.
(`OracleAiService.cs:477–497`, ה-prompt המלא ב-`OracleAiService.cs:634–641`.)

### שלב ב׳ — נורמליזציה שרתית (Inference)

לא סומכים על ה-AC בלבד. ב-`MapToAlgoTextFields` (`InvoiceService.cs:1201–1244`) השרת קובע מחדש את הקטגוריה:

1. אם `form_of_payment` שהוחזר הוא כבר אחד מ-`credit`/`cash`/`bank` → משתמשים בו.
2. אחרת — **מסיקים מתוך טקסט `payment.method`** ע״י סריקת מילות-מפתח (עברית ואנגלית):
   - מכיל `credit`/`card`/`visa`/`master`/`amex`/`emv`/`contactless`/`אשראי`/`כרטיס`/`סליקה`/`סליקת` → `credit`
   - מכיל `bank`/`transfer`/`העברה` → `bank`
   - אחרת → `cash` (ברירת מחדל).

ב-Edge Function אותו היגיון נמצא ב-`inferFormOfPayment()` (`analyze-invoice/index.ts:16–57`), עם מילון מורחב עוד יותר (`isracard`, `ישראכרט`, `diners`, `tap`, `chip`, `swipe`, `iban`).

### שלב ג׳ — פרטי כרטיס אשראי

**רק** אם הקטגוריה הסופית היא `credit`, מחלצים פרטי כרטיס; אחרת מאפסים אותם ל-`null`:

- **`card_last4`** — 4 ספרות אחרונות. נלקח מ-`payment.card_last4`, ואם חסר — מחלצים מתוך `method` בעזרת regex לתבניות ממוסכות (`****1234`, `XXXX-1234`, ספרות בסוף). ראו `analyze-invoice/index.ts:72–84` ו-`InvoiceService.cs:1232–1238`.
- **`card_type`** — רשת הכרטיס: `visa` | `mastercard` | `amex` | `diners` | `isracart` | `other`, לפי טקסט או BIN (Visa מתחיל ב-4, Mastercard ב-5, Amex ב-3). ראו `inferCardType()` ב-`analyze-invoice/index.ts:59–70`.

### שלב ד׳ — מיפוי ל-ID של הלקוח

זהו הלב של "איך אנחנו יודעים להגדיר בדיוק מה זה". הפונקציה `ResolveOptionIds` (`InvoiceService.cs:531–580`) קובעת את `FormOfPaymentId` ב-**שלושה מנגנוני נסיגה (fallback) לפי סדר עדיפות**:

1. **בחירת ה-AI** — אם ה-AI החזיר `form_of_payment_id` מספרי **שקיים** ברשימת הלקוח → משתמשים בו ישירות.
2. **התאמת שם מדויקת** — משווים את הקטגוריה (`credit`/`cash`/`bank`) מול שמות האופציות של הלקוח, אחרי נורמליזציה (`NormaliseFormOfPayment`: lowercase, החלפת `_` ברווח, ומילון aliases — `apple pay`/`google pay`/`samsung pay`/`credit card`/`visa`/`mastercard`… → `credit`; `bank transfer`/`wire transfer`/`העברה` → `bank`; `מזומן` → `cash`). ראו `InvoiceService.cs:433–459`.
3. **התאמה סמנטית לפי מילות-מפתח** — אם עדיין אין התאמה, מחפשים אופציה של הלקוח שהשם שלה **מכיל** מילת-מפתח שמתאימה לקטגוריה:
   - `credit` → שם שמכיל `c.c` / `cc` / `credit` / `card` / `amex` / `uatp` / `airplus` / `visa` / `master` / `diners`
   - `cash` → שם שמכיל `cash` / `מזומן` / `наличн`
   - `bank` → שם שמכיל `bank` / `transfer` / `wire` / `העברה`
   - **כלל טאי-ברייק:** מבין ההתאמות, מעדיפים אופציה שהשם שלה מכיל `emp`/`employee` (כרטיס עובד אישי) על פני כרטיס חברה. ראו `InvoiceService.cs:562–576`.

אם אף שלב לא הצליח → `FormOfPaymentId = null` (הקטגוריה הקנונית עדיין מוחזרת).

---

## 4. Expense Type — הגדרה מלאה

### שלב א׳ — חילוץ ע״י ה-AI

ה-prompt מגדיר את `expense_type` כ-enum סגור:

```
business_meal | vehicle | entertainment | hotel | internet | parking | meal | taxi | other
```

ה-AI בוחר את הקטגוריה המתאימה ביותר לפי אופי בית העסק והפריטים.
אם הלקוח שלח `ExpenseTypes`, נוסף בלוק:

```
EXPENSE TYPE OPTIONS (caller-supplied — pick the best matching ID for expense_type_id):
  12: "Business Meal"
  30: "Taxi"
- Return the numeric ID of the best match as "expense_type_id".
- If none fit, return null for expense_type_id.
```

(`OracleAiService.cs:465–474`, `:629`.)

### שלב ב׳ — ולידציה ל-enum מותר

ב-`MapToAlgoTextFields` (`InvoiceService.cs:1184–1187`):

1. קוראים את `expense_type` (או `category` כ-fallback), עושים `trim` + lowercase + החלפת רווחים ב-`_`.
2. אם הערך נמצא ברשימה הלבנה `ValidExpenseTypes` (`InvoiceService.cs:27–31`) → משתמשים בו.
3. אחרת → `"other"`.

כך אנחנו **מבטיחים** שהקטגוריה תמיד חוקית, גם אם ה-AI המציא ערך.

### שלב ג׳ — מיפוי ל-ID של הלקוח

ב-`ResolveOptionIds` (`InvoiceService.cs:497–529`), בשני מנגנוני נסיגה:

1. **בחירת ה-AI** — אם ה-AI החזיר `expense_type_id` מספרי **שקיים** ברשימת הלקוח → משתמשים בו.
2. **התאמת שם** — משווים את קטגוריית ה-`expense_type` (אחרי נורמליזציה: lowercase, `_` → רווח) מול שמות ה-`ExpenseTypes` של הלקוח, ולוקחים התאמה מדויקת.

אם אין התאמה → `expense_type_id = null`.

> שים לב: ל-Expense Type אין שלב "מילות-מפתח סמנטי" נפרד כמו ל-Form of Payment —
> ההתאמה מבוססת על שם קנוני מול שם הלקוח בלבד.

---

## 5. מבנה הפלט (JSON) שחוזר ללקוח

מתוך ה-AI (לפני מיפוי ה-ID):

```jsonc
{
  "payment": {
    "method": "סליקת אשראי VISA ****1234",
    "amount_paid": 117.00,
    "form_of_payment": "credit",
    "form_of_payment_id": 1,          // רק אם נשלחה רשימת FormOfPayments
    "card_last4": "1234",
    "card_type": "visa"
  },
  "expense_type": "business_meal",
  "expense_type_id": 12               // רק אם נשלחה רשימת ExpenseTypes
}
```

בפלט ה-API הסופי (`InvoiceController.cs:236–243`), כל שדה נחשף תחת **מספר שמות מקבילים** (PascalCase / camelCase / snake_case) כדי שכל לקוח combtas/AlgoText ימצא אותו ב-JsonPath:

- `FormOfPayment` / `formOfPayment` / `form_of_payment`
- `FormOfPaymentId` / `formOfPaymentId` / `form_of_payment_id`
- `ExpenseType` / `expenseType` / `expense_type` / `Category` / `category`
- `ExpenseTypeId` / `expenseTypeId` / `expense_type_id`
- `CardLast4` / `CardType` וכו׳.

שדות ה-Form of Payment וה-Expense Type הקנוניים **תמיד נוכחים** בפלט (גם אם ריקים), כדי ש-JsonPath לעולם לא ייכשל על "missing key".

---

## 6. טבלת מקרי-קצה והתנהגות ברירת-מחדל

| מצב | Form of Payment | form_of_payment_id | Expense Type | expense_type_id |
|------|-----------------|--------------------|--------------|-----------------|
| קבלה ללא אמצעי תשלום | `cash` (ברירת מחדל) | לפי מיפוי, אחרת `null` | לפי AI / `other` | לפי מיפוי, אחרת `null` |
| `"סליקת אשראי"` | `credit` (תמיד) | מיפוי, מועדף `emp`/employee | — | — |
| AI החזיר ID לא-קיים ברשימה | מתעלמים מה-ID, נופלים להתאמת-שם | מיפוי שרתי | — | — |
| ה-AI המציא expense_type לא חוקי | — | — | `other` | `null` |
| הלקוח לא שלח רשימות | הקטגוריה בלבד | `null` (לא מחושב) | הקטגוריה בלבד | `null` (לא מחושב) |
| כרטיס אשראי אך `card_last4` חסר | `credit` | — | — | — → מחלצים 4 ספרות מ-`method` ב-regex |
| קטגוריה ≠ credit | — | — | — | פרטי הכרטיס (`card_last4`,`card_type`) מאופסים ל-`null` |

---

## 7. היכן זה יושב בקוד

| קובץ | תפקיד |
|------|--------|
| `dotnet-backend/TripEx.Api/Services/OracleAiService.cs:375–641` | בניית ה-prompt — enum הקטגוריות, בלוקי ה-options של הלקוח, כללי form_of_payment |
| `dotnet-backend/TripEx.Api/Services/InvoiceService.cs:1184–1187` | ולידציית Expense Type מול `ValidExpenseTypes` |
| `dotnet-backend/TripEx.Api/Services/InvoiceService.cs:1201–1244` | נורמליזציית Form of Payment + חילוץ פרטי כרטיס |
| `dotnet-backend/TripEx.Api/Services/InvoiceService.cs:433–483` | מילון aliases + התאמת מילות-מפתח |
| `dotnet-backend/TripEx.Api/Services/InvoiceService.cs:491–581` | `ResolveOptionIds` — מיפוי הקטגוריה ל-ID של הלקוח (3 שכבות ל-FoP, 2 ל-ExpenseType) |
| `dotnet-backend/TripEx.Api/Controllers/InvoiceController.cs:236–243` | חשיפת השדות בפלט תחת כל ה-aliases |
| `dotnet-backend/TripEx.Api/Models/ApiModels.cs:64–180` | מודלים: `ExpenseTypeOption`, `FormOfPaymentOption`, `InvoiceFields` |
| `supabase/functions/analyze-invoice/index.ts:16–70` | גרסת Edge Function — `inferFormOfPayment`, `inferCardType`, `inferCardLast4` |
| `supabase/functions/analyze-invoice/index.ts:318–359` | חוקי ה-payment ב-prompt + פורמט הפלט |

---

*מסמך זה מתאר את הלוגיקה נכון לגרסת ה-prompt `v3` (`InvoiceService.PromptVersion`).*
*שינוי מהותי בלוגיקת ה-prompt מחייב העלאת הגרסה כדי לבטל את ה-cache.*
