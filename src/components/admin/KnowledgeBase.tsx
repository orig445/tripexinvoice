import { useState, useEffect, useCallback } from "react";
import {
  listKnowledgeDocuments,
  uploadKnowledgeFile,
  deleteKnowledgeDocument,
  processKnowledgeDocument,
  triggerKnowledgeSync,
  getKnowledgeDownloadUrl,
  type KnowledgeDoc,
  type KnowledgeAudience,
} from "@/lib/api-service";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { toast } from "sonner";
import {
  Upload,
  Trash2,
  FileText,
  Image,
  FileSpreadsheet,
  Loader2,
  Brain,
  X,
  CheckCircle2,
  AlertCircle,
  UploadCloud,
  RefreshCw,
  CloudDownload,
  Download,

} from "lucide-react";

// Domains (business area) — helps the agent decide WHEN a document is relevant.
const DOMAINS = [
  { value: "travel", label: "נסיעות והזמנות" },
  { value: "expenses", label: "הוצאות" },
  { value: "invoices", label: "חשבוניות ו-OCR" },
  { value: "billing", label: "תשלומים וחיוב" },
  { value: "policy", label: "מדיניות ונהלים" },
  { value: "account", label: "חשבון והרשאות" },
  { value: "technical", label: "טכני ותמיכה" },
  { value: "general", label: "כללי" },
];

// Types (kind of content).
const DOC_TYPES = [
  { value: "faq", label: "שאלות ותשובות" },
  { value: "guide", label: "מדריך / הדרכה" },
  { value: "policy", label: "מסמך מדיניות" },
  { value: "reference", label: "מידע עיוני / הגדרות" },
  { value: "troubleshooting", label: "פתרון תקלות" },
  { value: "other", label: "אחר" },
];

const labelFor = (list: { value: string; label: string }[], value: string | null) =>
  list.find((x) => x.value === value)?.label ?? value ?? "";

interface StagedFile {
  key: string;
  file: File;
  domain: string;
  docType: string;
  description: string;
  status: "idle" | "uploading" | "done" | "error";
  error?: string;
  chunks?: number;
}

const MAX_SIZE = 25 * 1024 * 1024;

export function KnowledgeBase({ audience = "external" }: { audience?: KnowledgeAudience }) {
  const [documents, setDocuments] = useState<KnowledgeDoc[]>([]);
  const [staged, setStaged] = useState<StagedFile[]>([]);
  const [isUploading, setIsUploading] = useState(false);
  const [isDragging, setIsDragging] = useState(false);

  const loadDocuments = useCallback(async () => {
    const { data, error } = await listKnowledgeDocuments(audience);
    if (error) {
      console.error("Failed to load documents:", error);
      return;
    }
    setDocuments(data);
  }, [audience]);

  useEffect(() => {
    loadDocuments();
  }, [loadDocuments]);

  const isZip = (f: File) =>
    /\.zip$/i.test(f.name) || f.type === "application/zip" || f.type === "application/x-zip-compressed";

  const SUPPORTED_EXT = /\.(pdf|docx?|xlsx?|csv|txt|md|json|xml)$/i;

  // Expand a ZIP archive into its individual documents (recursively skips
  // folders, macOS metadata and unsupported binaries).
  const expandZip = async (file: File): Promise<File[]> => {
    const JSZip = (await import("jszip")).default;
    const zip = await JSZip.loadAsync(file);
    const out: File[] = [];
    const entries = Object.values(zip.files) as any[];
    for (const entry of entries) {
      if (entry.dir) continue;
      const name = entry.name.split("/").pop() || entry.name;
      if (name.startsWith(".") || entry.name.startsWith("__MACOSX/")) continue;
      if (!SUPPORTED_EXT.test(name)) continue;
      const blob = await entry.async("blob");
      if (blob.size === 0 || blob.size > MAX_SIZE) continue;
      out.push(new File([blob], name, { type: blob.type || "application/octet-stream" }));
    }
    return out;
  };

  const addFiles = async (files: FileList | File[]) => {
    const expanded: File[] = [];
    for (const file of Array.from(files)) {
      if (isZip(file)) {
        try {
          const inner = await expandZip(file);
          if (inner.length === 0) {
            toast.error(`לא נמצאו קבצים נתמכים בתוך ${file.name}`);
            continue;
          }
          toast.success(`${file.name}: חולצו ${inner.length} קבצים`);
          expanded.push(...inner);
        } catch (err) {
          console.error("zip error", err);
          toast.error(`שגיאה בפתיחת ${file.name}`);
        }
        continue;
      }
      expanded.push(file);
    }

    const incoming: StagedFile[] = [];
    for (const file of expanded) {
      if (file.size > MAX_SIZE) {
        toast.error(`הקובץ ${file.name} גדול מדי (מקסימום 25MB)`);
        continue;
      }
      incoming.push({
        key: `${file.name}-${file.size}-${crypto.randomUUID()}`,
        file,
        domain: "general",
        docType: "reference",
        description: "",
        status: "idle",
      });
    }
    if (incoming.length) setStaged((prev) => [...prev, ...incoming]);
  };

  const handleFileInput = (e: React.ChangeEvent<HTMLInputElement>) => {
    if (e.target.files) addFiles(e.target.files);
    e.target.value = "";
  };

  const handleDrop = (e: React.DragEvent) => {
    e.preventDefault();
    setIsDragging(false);
    if (e.dataTransfer.files) addFiles(e.dataTransfer.files);
  };

  const patchStaged = (key: string, patch: Partial<StagedFile>) =>
    setStaged((prev) => prev.map((s) => (s.key === key ? { ...s, ...patch } : s)));

  const removeStaged = (key: string) =>
    setStaged((prev) => prev.filter((s) => s.key !== key));

  const uploadAll = async () => {
    const pending = staged.filter((s) => s.status === "idle" || s.status === "error");
    if (pending.length === 0) return;

    setIsUploading(true);
    let ok = 0;
    for (const item of pending) {
      patchStaged(item.key, { status: "uploading", error: undefined });
      const { data, error } = await uploadKnowledgeFile({
        file: item.file,
        domain: item.domain,
        docType: item.docType,
        description: item.description.trim() || undefined,
        audience,
      });

      if (error || data?.success === false) {
        patchStaged(item.key, {
          status: "error",
          error: error?.message || data?.error || "שגיאה בהעלאה",
        });
      } else {
        ok++;
        patchStaged(item.key, { status: "done", chunks: data?.chunksCreated ?? data?.ChunksCreated });
      }
    }

    setIsUploading(false);
    if (ok > 0) {
      toast.success(`${ok} קבצים הועלו לבסיס הידע של הסוכן`);
      await loadDocuments();
      // Clear the successfully-uploaded items after a short beat.
      setStaged((prev) => prev.filter((s) => s.status !== "done"));
    }
    if (ok < pending.length) toast.error(`${pending.length - ok} קבצים נכשלו`);
  };

  const handleDelete = async (doc: KnowledgeDoc) => {
    const { error } = await deleteKnowledgeDocument(doc.id);
    if (error) {
      toast.error("שגיאה במחיקת הקובץ");
      return;
    }
    toast.success("הקובץ נמחק");
    loadDocuments();
  };

  const [reprocessingId, setReprocessingId] = useState<string | null>(null);
  const [downloadingId, setDownloadingId] = useState<string | null>(null);

  const handleDownload = async (doc: KnowledgeDoc) => {
    setDownloadingId(doc.id);
    const { url, error } = await getKnowledgeDownloadUrl(doc);
    setDownloadingId(null);
    if (error || !url) {
      toast.error(error?.message || "לא ניתן להוריד את הקובץ");
      return;
    }
    const a = document.createElement("a");
    a.href = url;
    a.download = doc.file_name;
    a.target = "_blank";
    a.rel = "noopener";
    document.body.appendChild(a);
    a.click();
    a.remove();
  };

  const [isSyncing, setIsSyncing] = useState(false);
  const [bulk, setBulk] = useState<{ done: number; total: number } | null>(null);

  const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

  // Process a list of docs sequentially, throttled, with one backoff retry per
  // file — avoids hammering the AI gateway (rate limits) when running in bulk.
  const runBatch = async (docs: KnowledgeDoc[], label: string) => {
    if (docs.length === 0) return;
    setBulk({ done: 0, total: docs.length });
    let ok = 0;
    for (let i = 0; i < docs.length; i++) {
      let { error } = await processKnowledgeDocument(docs[i].id);
      if (error) {
        // one retry after a short backoff (helps with transient 429s)
        await sleep(3000);
        ({ error } = await processKnowledgeDocument(docs[i].id));
      }
      if (!error) ok++;
      setBulk({ done: i + 1, total: docs.length });
      await sleep(800); // throttle between files
    }
    setBulk(null);
    toast.success(`${label}: ${ok}/${docs.length} הצליחו${ok < docs.length ? `, ${docs.length - ok} עדיין נכשלו` : ""}`,
      { duration: 8000 });
    loadDocuments();
  };

  // Re-run the ingest pipeline (PII scrub + distill + auto-classify) on every
  // document currently listed — used to clean up files uploaded before distillation.
  const handleReprocessAll = () => runBatch([...documents], "זוקקו");

  // Retry only the documents that ended in "error".
  const handleRetryFailed = () => runBatch(documents.filter((d) => d.status === "error"), "שוחזרו");

  const handleSync = async () => {
    setIsSyncing(true);
    toast.info("מסנכרן מ-SharePoint ו-Zoho CRM...");
    const { data, error } = await triggerKnowledgeSync();
    setIsSyncing(false);
    if (error) {
      toast.error(`שגיאת סנכרון: ${error.message}`, { duration: 10000 });
    } else {
      const sums = (data?.summaries || []) as Array<any>;
      const totals = sums.reduce(
        (a, s) => ({ created: a.created + (s.created || 0), updated: a.updated + (s.updated || 0), errors: a.errors + (s.errors || 0) }),
        { created: 0, updated: 0, errors: 0 },
      );
      toast.success(`סנכרון הושלם: ${totals.created} חדשים, ${totals.updated} עודכנו${totals.errors ? `, ${totals.errors} שגיאות` : ""}`);
      // Surface per-source problems (e.g. "not configured") so setup gaps are visible.
      sums.filter((s) => !s.ok || s.message).forEach((s) => {
        if (s.message) toast.warning(`${s.source}: ${s.message}`, { duration: 12000 });
      });
    }
    loadDocuments();
  };

  const handleReprocess = async (doc: KnowledgeDoc) => {
    setReprocessingId(doc.id);
    toast.info(`מעבד מחדש את ${doc.file_name}...`);
    const { error } = await processKnowledgeDocument(doc.id);
    setReprocessingId(null);
    if (error) {
      toast.error(`שגיאה בעיבוד: ${error.message}`, { duration: 10000 });
    } else {
      toast.success("העיבוד הושלם");
    }
    loadDocuments();
  };

  const getFileIcon = (fileType: string, fileName: string) => {
    const t = (fileType + " " + fileName).toLowerCase();
    if (t.includes("image") || /\.(png|jpe?g|webp)$/.test(t)) return <Image className="h-4 w-4" />;
    if (t.includes("sheet") || t.includes("excel") || t.includes("csv") || /\.(xlsx?|csv)$/.test(t))
      return <FileSpreadsheet className="h-4 w-4" />;
    return <FileText className="h-4 w-4" />;
  };

  const getStatusBadge = (status: string) => {
    switch (status) {
      case "ready":
        return <Badge className="bg-green-600 hover:bg-green-600">מוכן</Badge>;
      case "processing":
        return (
          <Badge variant="secondary">
            <Loader2 className="h-3 w-3 animate-spin ml-1" />
            מעבד
          </Badge>
        );
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

  const pendingCount = staged.filter((s) => s.status === "idle" || s.status === "error").length;

  return (
    <div className="space-y-6">
      {/* ── Upload area ── */}
      <Card>
        <CardHeader>
          <div className="flex items-center gap-2">
            <Brain className="h-5 w-5 text-primary" />
            <CardTitle className="text-lg">העלאת קבצים לבסיס הידע</CardTitle>
          </div>
          <p className="text-sm text-muted-foreground">
            העלה כמה קבצים שתרצה. לכל קובץ בחר <strong>תחום</strong> ו<strong>סוג</strong>, והוסף
            הסבר קצר (אופציונלי) שיכוון את הסוכן מתי להשתמש בו. הקבצים נכנסים אוטומטית ל-RAG של הסוכן.
          </p>
        </CardHeader>
        <CardContent className="space-y-4">
          {/* Dropzone */}
          <div
            onDragOver={(e) => {
              e.preventDefault();
              setIsDragging(true);
            }}
            onDragLeave={() => setIsDragging(false)}
            onDrop={handleDrop}
            className={`rounded-lg border-2 border-dashed p-8 text-center transition-colors ${
              isDragging ? "border-primary bg-primary/5" : "border-muted-foreground/25"
            }`}
          >
            <UploadCloud className="h-10 w-10 mx-auto mb-3 text-muted-foreground" />
            <p className="text-sm mb-3">גרור לכאן קבצים או בחר מהמחשב</p>
            <input
              type="file"
              id="knowledge-upload"
              className="hidden"
              multiple
              accept=".pdf,.doc,.docx,.xls,.xlsx,.csv,.txt,.md,.json,.xml,.zip"
              onChange={handleFileInput}
            />
            <Button variant="outline" onClick={() => document.getElementById("knowledge-upload")?.click()}>
              <Upload className="h-4 w-4 ml-2" />
              בחר קבצים
            </Button>
            <p className="text-xs text-muted-foreground mt-3">
              PDF, Word, Excel, CSV, טקסט, Markdown, JSON, XML, ZIP · עד 25MB לקובץ
            </p>
          </div>

          {/* Staged files with per-file tagging */}
          {staged.length > 0 && (
            <div className="space-y-3">
              {staged.map((item) => (
                <div key={item.key} className="rounded-lg border bg-card p-3 space-y-3">
                  <div className="flex items-center justify-between gap-2">
                    <div className="flex items-center gap-2 min-w-0">
                      {getFileIcon(item.file.type, item.file.name)}
                      <span className="text-sm font-medium truncate">{item.file.name}</span>
                      <span className="text-xs text-muted-foreground flex-shrink-0">
                        {formatSize(item.file.size)}
                      </span>
                    </div>
                    <div className="flex items-center gap-2 flex-shrink-0">
                      {item.status === "uploading" && <Loader2 className="h-4 w-4 animate-spin text-primary" />}
                      {item.status === "done" && <CheckCircle2 className="h-4 w-4 text-green-600" />}
                      {item.status === "error" && (
                        <span title={item.error} className="flex items-center gap-1 text-destructive text-xs">
                          <AlertCircle className="h-4 w-4" />
                        </span>
                      )}
                      {item.status !== "uploading" && (
                        <Button variant="ghost" size="icon" className="h-7 w-7" onClick={() => removeStaged(item.key)}>
                          <X className="h-4 w-4" />
                        </Button>
                      )}
                    </div>
                  </div>

                  <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
                    <div className="space-y-1">
                      <Label className="text-xs">תחום</Label>
                      <Select value={item.domain} onValueChange={(v) => patchStaged(item.key, { domain: v })}>
                        <SelectTrigger className="h-9">
                          <SelectValue />
                        </SelectTrigger>
                        <SelectContent>
                          {DOMAINS.map((d) => (
                            <SelectItem key={d.value} value={d.value}>
                              {d.label}
                            </SelectItem>
                          ))}
                        </SelectContent>
                      </Select>
                    </div>
                    <div className="space-y-1">
                      <Label className="text-xs">סוג</Label>
                      <Select value={item.docType} onValueChange={(v) => patchStaged(item.key, { docType: v })}>
                        <SelectTrigger className="h-9">
                          <SelectValue />
                        </SelectTrigger>
                        <SelectContent>
                          {DOC_TYPES.map((t) => (
                            <SelectItem key={t.value} value={t.value}>
                              {t.label}
                            </SelectItem>
                          ))}
                        </SelectContent>
                      </Select>
                    </div>
                  </div>

                  <div className="space-y-1">
                    <Label className="text-xs">הסבר קצר לסוכן (אופציונלי)</Label>
                    <Input
                      placeholder="למשל: נוהל החזר הוצאות נסיעה לחו״ל 2026"
                      value={item.description}
                      onChange={(e) => patchStaged(item.key, { description: e.target.value })}
                    />
                  </div>

                  {item.status === "error" && item.error && (
                    <p className="text-xs text-destructive">{item.error}</p>
                  )}
                </div>
              ))}

              <div className="flex items-center justify-end gap-2">
                <Button variant="ghost" onClick={() => setStaged([])} disabled={isUploading}>
                  נקה הכל
                </Button>
                <Button onClick={uploadAll} disabled={isUploading || pendingCount === 0}>
                  {isUploading ? (
                    <Loader2 className="h-4 w-4 animate-spin ml-2" />
                  ) : (
                    <Upload className="h-4 w-4 ml-2" />
                  )}
                  העלה {pendingCount > 0 ? `(${pendingCount})` : ""}
                </Button>
              </div>
            </div>
          )}
        </CardContent>
      </Card>

      {/* ── Existing documents ── */}
      <Card>
        <CardHeader>
          <div className="flex items-center justify-between gap-2 flex-wrap">
            <CardTitle className="text-lg">קבצים בבסיס הידע ({documents.length})</CardTitle>
            <div className="flex items-center gap-2">
              {documents.some((d) => d.status === "error") && (
                <Button variant="outline" size="sm" className="gap-2 text-amber-700 border-amber-300" onClick={handleRetryFailed} disabled={!!bulk}>
                  <RefreshCw className={`h-4 w-4 ${bulk ? "animate-spin" : ""}`} />
                  {bulk ? `משחזר ${bulk.done}/${bulk.total}...` : `נסה שוב את השגיאות (${documents.filter((d) => d.status === "error").length})`}
                </Button>
              )}
              {documents.length > 0 && (
                <Button variant="outline" size="sm" className="gap-2" onClick={handleReprocessAll} disabled={!!bulk}>
                  <RefreshCw className={`h-4 w-4 ${bulk ? "animate-spin" : ""}`} />
                  {bulk ? `מזקק ${bulk.done}/${bulk.total}...` : "נקה וזקק הכל"}
                </Button>
              )}
              {audience === "internal" && (
                <Button variant="outline" size="sm" className="gap-2" onClick={handleSync} disabled={isSyncing}>
                  <CloudDownload className={`h-4 w-4 ${isSyncing ? "animate-pulse" : ""}`} />
                  {isSyncing ? "מסנכרן..." : "סנכרן מ-SharePoint / Zoho"}
                </Button>
              )}
            </div>
          </div>
        </CardHeader>
        <CardContent>
          {documents.length === 0 ? (
            <div className="text-center py-8 text-muted-foreground">
              <Brain className="h-12 w-12 mx-auto mb-3 opacity-30" />
              <p>אין עדיין קבצים בבסיס הידע</p>
            </div>
          ) : (
            <div className="space-y-2">
              {documents.map((doc) => (
                <div
                  key={doc.id}
                  className="flex items-center justify-between p-3 rounded-lg border bg-card hover:bg-accent/5 transition-colors gap-3"
                >
                  <div className="flex items-center gap-3 min-w-0">
                    <div className="text-muted-foreground">{getFileIcon(doc.file_type, doc.file_name)}</div>
                    <div className="min-w-0">
                      <p className="text-sm font-medium truncate">{doc.file_name}</p>
                      <div className="flex items-center gap-1.5 flex-wrap mt-1">
                        {doc.source && doc.source !== "upload" && (
                          <Badge className="text-[10px] bg-blue-600 hover:bg-blue-600 gap-1">
                            <CloudDownload className="h-2.5 w-2.5" />
                            {doc.source === "sharepoint" ? "SharePoint" : doc.source === "zoho_crm" ? "Zoho CRM" : doc.source}
                          </Badge>
                        )}
                        {doc.domain && (
                          <Badge variant="secondary" className="text-[10px]">
                            {labelFor(DOMAINS, doc.domain)}
                          </Badge>
                        )}
                        {doc.doc_type && (
                          <Badge variant="outline" className="text-[10px]">
                            {labelFor(DOC_TYPES, doc.doc_type)}
                          </Badge>
                        )}
                        <span className="text-xs text-muted-foreground">{formatSize(doc.file_size)}</span>
                      </div>
                      {doc.description && (
                        <p className="text-xs text-muted-foreground mt-1 truncate">💡 {doc.description}</p>
                      )}
                    </div>
                  </div>
                  <div className="flex items-center gap-1.5 flex-shrink-0">
                    {getStatusBadge(doc.status)}
                    <Button
                      variant="ghost"
                      size="icon"
                      className="h-8 w-8"
                      title="הורד קובץ"
                      disabled={downloadingId === doc.id}
                      onClick={() => handleDownload(doc)}
                    >
                      {downloadingId === doc.id ? (
                        <Loader2 className="h-4 w-4 animate-spin" />
                      ) : (
                        <Download className="h-4 w-4" />
                      )}
                    </Button>
                    <Button

                      variant="ghost"
                      size="icon"
                      className="h-8 w-8"
                      title="עבד מחדש"
                      disabled={reprocessingId === doc.id}
                      onClick={() => handleReprocess(doc)}
                    >
                      <RefreshCw className={`h-4 w-4 ${reprocessingId === doc.id ? "animate-spin" : ""}`} />
                    </Button>
                    <Button
                      variant="ghost"
                      size="icon"
                      className="h-8 w-8 text-destructive hover:text-destructive"
                      title="מחק"
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
    </div>
  );
}
