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
    <div className="min-h-screen bg-background">
      <Header />
      <main className="container py-6 md:py-10 space-y-6 max-w-4xl">
        <div className="flex items-start gap-3">
          <div className="w-11 h-11 rounded-xl bg-primary/10 flex items-center justify-center flex-shrink-0">
            <Brain className="h-6 w-6 text-primary" />
          </div>
          <div>
            <h1 className="text-2xl font-bold">Knowledge center — customer agent</h1>
            <p className="text-muted-foreground">
              Knowledge base for the <strong>customer-facing</strong> chatbot. Upload documents and they are indexed automatically
              into RAG. Tag each file with a domain and type and add a short description so the agent knows when to use it.
            </p>
          </div>
        </div>

        <KnowledgeBase audience="external" />
      </main>
    </div>
  );
};

export default KnowledgeUpload;
