

# AI Router - ארכיטקטורה חדשה

## סיכום

שינוי הארכיטקטורה כך שה-Edge Function החדשה `ai-router` תהיה נקודת הכניסה המרכזית לכל אינטראקציה עם ה-AI. ה-OCR (analyze-invoice) יישאר כמודול נפרד שנקרא מתוך ה-router כשצריך.

## מה ישתנה

### 1. Edge Function חדשה: `ai-router`

תחליף את `tripex-chat` כנקודת הכניסה המרכזית.

**Input חדש (POST):**
```text
{
  "source": "mobile | web | bi | tas",
  "scope": "current module",
  "trid": "",
  "text": "",
  "type": "text | image | audio",
  "sessionToken": ""
}
```

**Output אחיד:**
```text
{
  "actions": [],
  "text": "",
  "redirectPage": "",
  "data": {},
  "session_id": ""
}
```

**Action Mapping:**
- help -> actions: ["Redirect"], redirectPage: "help"
- scan -> actions: ["Camera"], text: "Scan your receipt"
- expense -> actions: ["AddExpense"]
- bi -> actions: ["DisplayResults"]
- online -> actions: ["Redirect"], redirectPage: "booking"

**OCR Flow:** כשה-type הוא "image", ה-router יקרא ל-`analyze-invoice` פנימית (HTTP call), ויחזיר את התוצאה עם actions: ["AddExpense"] + data מהפענוח.

**Session Handling:** ימשיך להשתמש בטבלאות `chat_sessions` ו-`chat_messages` הקיימות. sessionToken יתמפה ל-session_id.

**Logging:** כל בקשה תתועד ב-`chatbot_logs` עם: request, detected intent, actions returned, errors.

**Default Response:** "Hello, I'm TripEX AI. How can I assist you today?"

### 2. מבנה לאינטגרציות עתידיות (Oracle TAS)

הכנת פונקציות stub בתוך ה-router:
- `fetchTASData(userId)` - שליפת נתוני נסיעות
- `fetchTRDetails(trId)` - פרטי TR
- `validateApproval(trId)` - בדיקת אישורים
- `submitExpense(data)` - הגשת הוצאה

כל אלה יחזירו placeholder בשלב זה עם הערה `// TODO: Connect to Oracle TAS API`.

### 3. עדכון Frontend

- `useChatbot.ts` - עדכון ה-hook לקרוא ל-`ai-router` במקום `tripex-chat`, ולעבוד עם הפורמט החדש (actions array, redirectPage, data).
- `ChatWindow.tsx` - עדכון `handleAction` לתמוך ב-actions החדשים (Camera, Redirect, AddExpense, DisplayResults).
- `ChatMessage.tsx` - עדכון כפתורי הפעולה להתאים ל-action mapping החדש.

### 4. config.toml

הוספת ה-function החדשה:
```text
[functions.ai-router]
verify_jwt = false
```

### 5. Edge Function ישנה

`tripex-chat` תישאר זמינה לתאימות לאחור אבל לא תהיה בשימוש מה-frontend.

---

## פרטים טכניים

### מבנה ai-router/index.ts

```text
1. Parse input (source, scope, trid, text, type, sessionToken)
2. Authenticate user via Authorization header
3. If type === "image":
     -> Call analyze-invoice internally
     -> Return { actions: ["AddExpense"], data: extractedFields }
4. Else:
     -> Send text + history to Oracle AI with intent detection prompt
     -> Parse AI response (intent + text)
     -> Map intent to action using ACTION_MAPPING
     -> Return { actions, text, redirectPage, data }
5. Log everything to chatbot_logs
```

### ACTION_MAPPING (hardcoded, not AI-dependent)

```text
help     -> { actions: ["Redirect"],       redirectPage: "help" }
scan     -> { actions: ["Camera"],         text: "Scan your receipt" }
expense  -> { actions: ["AddExpense"] }
bi       -> { actions: ["DisplayResults"] }
online   -> { actions: ["Redirect"],       redirectPage: "booking" }
general  -> { actions: [] }
```

ה-AI מזהה את ה-intent, וה-router ממפה אותו לפעולות - הפרדה ברורה בין זיהוי לביצוע.

