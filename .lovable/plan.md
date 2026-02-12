

# TripEX AI Chatbot - תוכנית מלאה

## סקירה כללית

בניית מערכת צ'אטבוט AI מלאה ל-TripEX הכוללת:
- פאנל שליטה למנהל (הגדרות הבוט, פרומפטים, לוגים)
- ממשק צ'אטבוט צף (Web + Mobile) עם אווטאר
- Backend חכם עם זיהוי כוונות דרך Oracle AI
- שמירת היסטוריית שיחות

---

## שלב 1: מסד נתונים

### טבלאות חדשות:

**`chatbot_config`** - הגדרות הבוט (שורה אחת למנהל)
- `id`, `bot_name` (ברירת מחדל: "TripEX AI"), `avatar_url`, `welcome_message`, `system_prompt`, `model_name`, `max_tokens`, `temperature`, `is_active`, `created_at`, `updated_at`

**`chat_sessions`** - סשנים של שיחות
- `id`, `user_id`, `source` (web/mobile), `status` (active/closed), `created_at`, `updated_at`

**`chat_messages`** - הודעות בשיחה
- `id`, `session_id` (FK to chat_sessions), `role` (user/assistant/system), `content`, `intent` (help/scan/bi/online/expense/general), `metadata` (JSON - actions, redirects), `created_at`

**`chatbot_logs`** - לוגים למנהל
- `id`, `session_id`, `user_id`, `event_type` (intent_detected/error/action), `details` (JSON), `created_at`

### RLS:
- משתמשים רואים רק את הסשנים וההודעות שלהם
- מנהלים רואים הכל (config, logs)
- Realtime מופעל על `chat_messages`

---

## שלב 2: Edge Function - `tripex-chat`

פונקציה חדשה `supabase/functions/tripex-chat/index.ts` שמטפלת בכל הלוגיקה:

1. **קבלת הודעה** מהמשתמש עם `session_id` ו-`message`
2. **טעינת system prompt** מטבלת `chatbot_config`
3. **שליחה ל-Oracle AI** (Chicago region, `meta.llama-4-maverick`) עם:
   - System prompt לזיהוי כוונות
   - היסטוריית השיחה האחרונה (10 הודעות אחרונות)
4. **פירוש התשובה**: זיהוי אם התשובה היא פקודה (help/scan/bi/online/expense) או שיחה חופשית
5. **שמירת ההודעה והתשובה** ב-DB
6. **החזרת JSON** עם:
   - `text` - תשובת הבוט
   - `action` - פעולה (redirect/scan/camera/none)
   - `intent` - הכוונה שזוהתה

### System Prompt (ברירת מחדל):

```text
You are TripEX AI, a Personal Assistant for Travel & Expense Management.
Detect user intent and respond accordingly:

- Help/guidance -> respond: {"intent": "help", "action": "none"}
- Scan receipt -> respond: {"intent": "scan", "action": "camera"}
- Analyze data (BI) -> respond: {"intent": "bi", "action": "none"}
- Online booking -> respond: {"intent": "online", "action": "redirect"}
- Manage expenses -> respond: {"intent": "expense", "action": "redirect"}
- General chat -> respond naturally with {"intent": "general", "action": "none"}

Always respond in the user's language. Return JSON with: intent, action, text
```

---

## שלב 3: ממשק צ'אטבוט צף

### קומפוננטות חדשות:

**`ChatbotWidget`** - הכפתור הצף + חלון הצ'אט
- כפתור עגול עם אווטאר בפינה הימנית התחתונה
- אנימציית bounce קלה כשנפתח
- לחיצה פותחת חלון צ'אט
- Responsive - עובד גם במובייל

**`ChatWindow`** - חלון השיחה
- Header עם שם הבוט ואווטאר
- אזור הודעות עם scroll
- הודעת פתיחה אוטומטית (welcome message)
- Input field + כפתור שליחה
- אינדיקטור "מקליד..."
- כפתורי פעולה מהירה (סרוק קבלה, הוסף הוצאה, עזרה)

**`ChatMessage`** - הודעה בודדת
- עיצוב שונה לuser vs assistant
- תמיכה ב-Markdown (react-markdown לא נדרש, נשתמש בעיצוב פשוט)
- כפתורי פעולה מוטמעים (אם הבוט מחזיר action)

### תזרים:
1. משתמש לוחץ על האווטאר -> נפתח חלון צ'אט
2. הודעת ברוכים הבאים מוצגת
3. משתמש כותב -> שליחה ל-Edge Function
4. תשובה מוצגת + פעולה אם יש (פתיחת סורק, הפניה לדף)

---

## שלב 4: פאנל שליטה למנהל

### דף חדש: `/admin/chatbot`

נגיש רק למנהלים (role === "admin"). כולל טאבים:

**טאב הגדרות:**
- שם הבוט
- העלאת אווטאר
- הודעת פתיחה
- System Prompt (textarea גדול)
- הגדרות מודל (temperature, max_tokens)
- מתג הפעלה/כיבוי

**טאב לוגים:**
- טבלת לוגים עם פילטרים (תאריך, משתמש, כוונה)
- סטטיסטיקות: כמות שיחות, כוונות נפוצות

**טאב שיחות:**
- רשימת סשנים אחרונים
- צפייה בשיחה מלאה

---

## שלב 5: ניווט ונתיבים

- הוספת Route `/admin/chatbot` ב-App.tsx (מוגן למנהלים)
- הוספת לינק בHeader למנהלים ("פאנל צ'אטבוט")
- הצ'אטבוט הצף מופיע בכל הדפים (מוזרק ב-Index)

---

## פרטים טכניים

### קבצים חדשים:
```text
src/pages/AdminChatbot.tsx          - דף פאנל השליטה
src/components/chatbot/ChatbotWidget.tsx  - כפתור צף + wrapper
src/components/chatbot/ChatWindow.tsx     - חלון הצ'אט
src/components/chatbot/ChatMessage.tsx    - הודעה בודדת
src/components/chatbot/ChatInput.tsx      - שדה קלט
src/components/chatbot/QuickActions.tsx   - כפתורי פעולה מהירה
src/components/admin/ChatbotSettings.tsx  - טאב הגדרות
src/components/admin/ChatbotLogs.tsx      - טאב לוגים
src/components/admin/ChatbotSessions.tsx  - טאב שיחות
src/hooks/useChatbot.ts                  - Hook לניהול הצ'אט
supabase/functions/tripex-chat/index.ts  - Edge Function
```

### קבצים שישתנו:
```text
src/App.tsx          - הוספת Route לפאנל
src/pages/Index.tsx  - הוספת ChatbotWidget
src/components/Header.tsx - לינק לפאנל למנהלים
supabase/config.toml - הוספת tripex-chat function
```

### Secret קיים בשימוש:
- `oracleapikey_2` - אותו מפתח Oracle AI שכבר מוגדר (Chicago region)

### אין צורך בחבילות חדשות
- הכל נבנה עם Shadcn/UI, Lucide icons, ו-Tailwind הקיימים

