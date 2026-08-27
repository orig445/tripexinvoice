import { Header } from "@/components/Header";
import { KnowledgeBase } from "@/components/admin/KnowledgeBase";
import { Lock } from "lucide-react";

/**
 * Standalone knowledge-upload page for the INTERNAL chatbot (route: /knowledge-internal).
 *
 * Documents uploaded here are tagged audience="internal" and are kept separate
 * from the customer-facing agent's knowledge base. The customer bot's RAG only
 * retrieves audience="external" (or legacy untagged) documents, so internal
 * material never leaks into answers given to customers.
 */
const KnowledgeUploadInternal = () => {
  return (
    <div className="min-h-screen bg-background">
      <Header />
      <main className="container py-6 md:py-10 space-y-6 max-w-4xl">
        <div className="flex items-start gap-3">
          <div className="w-11 h-11 rounded-xl bg-amber-500/10 flex items-center justify-center flex-shrink-0">
            <Lock className="h-6 w-6 text-amber-600" />
          </div>
          <div>
            <h1 className="text-2xl font-bold">Knowledge center — internal agent</h1>
            <p className="text-muted-foreground">
              Knowledge base for the <strong>internal</strong> chatbot (employees only). Documents uploaded here
              are fully separated from the customer agent knowledge base — they will <strong>not</strong> be exposed to customers.
            </p>
          </div>
        </div>

        <KnowledgeBase audience="internal" />
      </main>
    </div>
  );
};

export default KnowledgeUploadInternal;
