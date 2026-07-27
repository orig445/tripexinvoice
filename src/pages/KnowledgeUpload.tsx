import { Header } from "@/components/Header";
import { KnowledgeBase } from "@/components/admin/KnowledgeBase";
import { Brain } from "lucide-react";

/**
 * Standalone knowledge-upload page (route: /knowledge).
 *
 * Unlike the admin chatbot panel, this page is reachable by any signed-in team
 * member so that content owners — not just developers — can upload documents
 * straight into the agent's RAG. Uploading here runs the full ingest pipeline
 * (store → extract text → chunk → index) automatically.
 */
const KnowledgeUpload = () => {
  return (
    <div className="min-h-screen bg-background" dir="rtl">
      <Header />
      <main className="container py-6 md:py-10 space-y-6 max-w-4xl">
        <div className="flex items-start gap-3">
          <div className="w-11 h-11 rounded-xl bg-primary/10 flex items-center justify-center flex-shrink-0">
            <Brain className="h-6 w-6 text-primary" />
          </div>
          <div>
            <h1 className="text-2xl font-bold">מרכז הידע — סוכן הלקוחות</h1>
            <p className="text-muted-foreground">
              בסיס הידע של הצ'אטבוט <strong>הפונה ללקוחות</strong>. העלו מסמכים והם ייכנסו אוטומטית
              ל-RAG. תייגו כל קובץ לתחום ולסוג והוסיפו הסבר קצר — כך הסוכן ידע מתי להשתמש בכל מסמך.
            </p>
          </div>
        </div>

        <KnowledgeBase audience="external" />
      </main>
    </div>
  );
};

export default KnowledgeUpload;
