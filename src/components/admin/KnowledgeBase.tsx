import { useState, useEffect, useCallback } from "react";
import { supabase } from "@/integrations/supabase/client";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { toast } from "sonner";
import { Upload, Trash2, FileText, Image, FileSpreadsheet, Loader2, Brain, RefreshCw, Download } from "lucide-react";

interface KnowledgeDocument {
  id: string;
  file_name: string;
  file_type: string;
  file_url: string;
  file_size: number | null;
  status: string;
  created_at: string;
}

export function KnowledgeBase() {
  const [documents, setDocuments] = useState<KnowledgeDocument[]>([]);
  const [isUploading, setIsUploading] = useState(false);
  const [processingIds, setProcessingIds] = useState<Set<string>>(new Set());

  const loadDocuments = useCallback(async () => {
    const { data, error } = await supabase
      .from("knowledge_documents")
      .select("*")
      .order("created_at", { ascending: false });

    if (error) {
      console.error("Failed to load documents:", error);
      return;
    }
    setDocuments((data || []) as KnowledgeDocument[]);
  }, []);

  useEffect(() => {
    loadDocuments();
  }, [loadDocuments]);

  // Poll for processing status
  useEffect(() => {
    if (processingIds.size === 0) return;
    const interval = setInterval(loadDocuments, 3000);
    return () => clearInterval(interval);
  }, [processingIds, loadDocuments]);

  // Update processing IDs based on documents
  useEffect(() => {
    const processing = new Set(
      documents.filter((d) => d.status === "processing" || d.status === "pending").map((d) => d.id)
    );
    setProcessingIds(processing);
  }, [documents]);

  const handleUpload = async (e: React.ChangeEvent<HTMLInputElement>) => {
    const files = e.target.files;
    if (!files || files.length === 0) return;

    setIsUploading(true);
    try {
      for (const file of Array.from(files)) {
        if (file.size > 20 * 1024 * 1024) {
          toast.error(`הקובץ ${file.name} גדול מדי (מקסימום 20MB)`);
          continue;
        }

        // Sanitize filename - remove non-ASCII chars, keep extension
        const ext = file.name.split(".").pop() || "bin";
        const safeName = `${crypto.randomUUID()}.${ext}`;
        const filePath = safeName;

        // Upload to storage
        const { error: uploadErr } = await supabase.storage
          .from("knowledge")
          .upload(filePath, file);

        if (uploadErr) {
          toast.error(`שגיאה בהעלאת ${file.name}: ${uploadErr.message}`);
          continue;
        }

        // Create document record
        const { data: doc, error: docErr } = await supabase
          .from("knowledge_documents")
          .insert({
            file_name: file.name,
            file_type: file.type,
            file_url: filePath,
            file_size: file.size,
            uploaded_by: (await supabase.auth.getUser()).data.user?.id,
          })
          .select("id")
          .single();

        if (docErr) {
          toast.error(`שגיאה ביצירת רשומה ל-${file.name}`);
          continue;
        }

        // Trigger processing
        supabase.functions.invoke("process-knowledge", {
          body: { document_id: doc.id },
        }).catch((err) => {
          console.error("Processing trigger error:", err);
        });

        toast.success(`${file.name} הועלה ונמצא בעיבוד`);
      }

      await loadDocuments();
    } finally {
      setIsUploading(false);
      // Reset input
      e.target.value = "";
    }
  };

  const handleDelete = async (doc: KnowledgeDocument) => {
    const { error: storageErr } = await supabase.storage
      .from("knowledge")
      .remove([doc.file_url]);

    if (storageErr) {
      console.error("Storage delete error:", storageErr);
    }

    const { error: dbErr } = await supabase
      .from("knowledge_documents")
      .delete()
      .eq("id", doc.id);

    if (dbErr) {
      toast.error("שגיאה במחיקת הקובץ");
      return;
    }

    toast.success("הקובץ נמחק");
    loadDocuments();
  };

  const handleReprocess = async (doc: KnowledgeDocument) => {
    toast.info(`מעבד מחדש ${doc.file_name}...`);
    const { error } = await supabase.functions.invoke("process-knowledge", {
      body: { document_id: doc.id },
    });
    if (error) {
      toast.error("שגיאה בעיבוד מחדש");
    } else {
      toast.success("העיבוד הושלם");
      loadDocuments();
    }
  };

  const handleDownload = async (doc: KnowledgeDocument) => {
    const { data, error } = await supabase.storage
      .from("knowledge")
      .download(doc.file_url);

    if (error || !data) {
      toast.error("שגיאה בהורדת הקובץ");
      return;
    }

    const url = URL.createObjectURL(data);
    const a = document.createElement("a");
    a.href = url;
    a.download = doc.file_name;
    a.click();
    URL.revokeObjectURL(url);
  };

  const getFileIcon = (fileType: string) => {
    if (fileType.includes("image")) return <Image className="h-4 w-4" />;
    if (fileType.includes("spreadsheet") || fileType.includes("excel") || fileType.includes("csv"))
      return <FileSpreadsheet className="h-4 w-4" />;
    return <FileText className="h-4 w-4" />;
  };

  const getStatusBadge = (status: string) => {
    switch (status) {
      case "ready":
        return <Badge variant="default" className="bg-green-600">מוכן</Badge>;
      case "processing":
        return <Badge variant="secondary"><Loader2 className="h-3 w-3 animate-spin mr-1" />מעבד</Badge>;
      case "pending":
        return <Badge variant="outline">ממתין</Badge>;
      case "error":
        return <Badge variant="destructive">שגיאה</Badge>;
      default:
        return <Badge variant="outline">{status}</Badge>;
    }
  };

  const formatSize = (bytes: number | null) => {
    if (!bytes) return "";
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
    return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  };

  return (
    <Card>
      <CardHeader>
        <div className="flex items-center justify-between">
          <div className="flex items-center gap-2">
            <Brain className="h-5 w-5 text-primary" />
            <CardTitle className="text-lg">בסיס ידע (Knowledge Base)</CardTitle>
          </div>
          <div>
            <input
              type="file"
              id="knowledge-upload"
              className="hidden"
              multiple
              accept=".pdf,.doc,.docx,.xls,.xlsx,.csv,.txt,.md,.json,.xml,.png,.jpg,.jpeg,.webp"
              onChange={handleUpload}
            />
            <Button
              onClick={() => document.getElementById("knowledge-upload")?.click()}
              disabled={isUploading}
              className="gap-2"
            >
              {isUploading ? (
                <Loader2 className="h-4 w-4 animate-spin" />
              ) : (
                <Upload className="h-4 w-4" />
              )}
              העלה קבצים
            </Button>
          </div>
        </div>
        <p className="text-sm text-muted-foreground">
          העלה קבצים למוח של הסוכן - PDF, Word, Excel, CSV, טקסט, תמונות
        </p>
      </CardHeader>
      <CardContent>
        {documents.length === 0 ? (
          <div className="text-center py-8 text-muted-foreground">
            <Brain className="h-12 w-12 mx-auto mb-3 opacity-30" />
            <p>אין קבצים בבסיס הידע</p>
            <p className="text-sm mt-1">העלה קבצים כדי שהסוכן יוכל לענות על שאלות על סמך המידע שלך</p>
          </div>
        ) : (
          <div className="space-y-2">
            {documents.map((doc) => (
              <div
                key={doc.id}
                className="flex items-center justify-between p-3 rounded-lg border bg-card hover:bg-accent/5 transition-colors"
              >
                <div className="flex items-center gap-3 min-w-0">
                  <div className="text-muted-foreground">{getFileIcon(doc.file_type)}</div>
                  <div className="min-w-0">
                    <p className="text-sm font-medium truncate">{doc.file_name}</p>
                    <p className="text-xs text-muted-foreground">
                      {formatSize(doc.file_size)} · {new Date(doc.created_at).toLocaleDateString("he-IL")}
                    </p>
                  </div>
                </div>
                <div className="flex items-center gap-1.5 flex-shrink-0">
                  {getStatusBadge(doc.status)}
                  <Button
                    variant="ghost"
                    size="icon"
                    className="h-8 w-8"
                    title="הורד"
                    onClick={() => handleDownload(doc)}
                  >
                    <Download className="h-4 w-4" />
                  </Button>
                  <Button
                    variant="ghost"
                    size="icon"
                    className="h-8 w-8"
                    title="עבד מחדש"
                    onClick={() => handleReprocess(doc)}
                  >
                    <RefreshCw className="h-4 w-4" />
                  </Button>
                  <Button
                    variant="ghost"
                    size="icon"
                    className="h-8 w-8 text-destructive hover:text-destructive"
                    onClick={() => handleDelete(doc)}
                  >
                    <Trash2 className="h-4 w-4" />
                  </Button>
                </div>
              </div>
            ))}
          </div>
        )}
      </CardContent>
    </Card>
  );
}
