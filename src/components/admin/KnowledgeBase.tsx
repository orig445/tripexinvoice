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
  { value: "travel", label: "Travel & bookings" },
  { value: "expenses", label: "Expenses" },
  { value: "invoices", label: "Invoices & OCR" },
  { value: "billing", label: "Payments & billing" },
  { value: "policy", label: "Policies & procedures" },
  { value: "account", label: "Account & permissions" },
  { value: "technical", label: "Technical & support" },
  { value: "general", label: "General" },
];

// Types (kind of content).
const DOC_TYPES = [
  { value: "faq", label: "FAQ" },
  { value: "guide", label: "Guide / tutorial" },
  { value: "policy", label: "Policy document" },
  { value: "reference", label: "Reference / definitions" },
  { value: "troubleshooting", label: "Troubleshooting" },
  { value: "other", label: "Other" },
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
  const [autoUpload, setAutoUpload] = useState(false);


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

  const SUPPORTED_EXT = /\.(pdf|docx?|xlsx?|pptx?|csv|tsv|txt|md|log|json|xml|html?|eml|rtf)$/i;

  const mimeForName = (name: string) => {
    const ext = name.split(".").pop()?.toLowerCase();
    const types: Record<string, string> = {
      pdf: "application/pdf", doc: "application/msword",
      docx: "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
      xls: "application/vnd.ms-excel",
      xlsx: "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
      csv: "text/csv", tsv: "text/tab-separated-values", txt: "text/plain",
      md: "text/markdown", json: "application/json", xml: "application/xml",
      html: "text/html", htm: "text/html", zip: "application/zip",
    };
    return (ext && types[ext]) || "application/octet-stream";
  };

  // zip.js reads directly from the browser Blob in ranges. Unlike JSZip, it
  // does not first allocate an ArrayBuffer as large as the entire archive.
  const expandZip = async (file: File | Blob, depth = 0): Promise<File[]> => {
    const { BlobReader, BlobWriter, ZipReader } = await import("@zip.js/zip.js");
    const reader = new ZipReader(new BlobReader(file));
    const out: File[] = [];
    try {
      const entries = await reader.getEntries();
      for (const entry of entries) {
        if (entry.directory || !("getData" in entry)) continue;
        const name = entry.filename.split("/").pop() || entry.filename;
        if (name.startsWith(".") || entry.filename.startsWith("__MACOSX/")) continue;
        if (entry.encrypted) throw new Error("The archive is password protected");

        const isNestedZip = /\.zip$/i.test(name) && depth < 3;
        if (!isNestedZip && !SUPPORTED_EXT.test(name)) continue;
        // Reject oversized expanded entries before allocating their contents.
        if (!isNestedZip && (!entry.uncompressedSize || entry.uncompressedSize > MAX_SIZE)) continue;
        try {
          const blob = await entry.getData(new BlobWriter(mimeForName(name)));
          if (isNestedZip) {
            out.push(...(await expandZip(blob, depth + 1)));
          } else if (blob.size > 0 && blob.size <= MAX_SIZE) {
            out.push(new File([blob], name, { type: mimeForName(name) }));
          }
        } catch (err) {
          console.error("zip entry error", name, err);
        }
      }
      return out;
    } finally {
      await reader.close();
    }
  };

  const addFiles = async (files: FileList | File[]) => {
    let cameFromZip = false;

    const expanded: File[] = [];
    for (const file of Array.from(files)) {
      if (isZip(file)) {
        try {
          const inner = await expandZip(file);
          if (inner.length === 0) {
            toast.error(`No supported files found inside ${file.name}`);
            continue;
          }
          toast.success(`${file.name}: extracted ${inner.length} files`);
          expanded.push(...inner);
        } catch (err) {
          console.error("zip error", err);
          const msg = err instanceof Error ? err.message : String(err);
          toast.error(`Failed to open ${file.name}`, {
            description: /encrypted|password/i.test(msg)
              ? "The archive is password protected — unzip it locally and upload the files."
              : /array buffer allocation|out of memory|allocation failed/i.test(msg)
                ? "This archive is too large for this browser. Extract it on your computer, then upload the resulting files in batches."
              : /notreadable|could not be read|permission/i.test(msg)
                ? "The browser lost access to the file. Copy the ZIP to your Desktop (not iCloud/Downloads sync or an external drive) and pick it again."
                : msg.slice(0, 180),
          });

        }

        continue;
      }
      expanded.push(file);
    }

    const incoming: StagedFile[] = [];
    for (const file of expanded) {
      if (file.size > MAX_SIZE) {
        toast.error(`File ${file.name} is too large (max 25MB)`);
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
    let done = 0;
    const total = pending.length;
    const toastId = `kb-upload-${Date.now()}`;
    if (total > 1) toast.loading(`Uploading 0/${total} files…`, { id: toastId });

    const uploadOne = async (item: StagedFile) => {
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
          error: error?.message || data?.error || "Upload failed",
        });
      } else {
        ok++;
        patchStaged(item.key, { status: "done", chunks: data?.chunksCreated ?? data?.ChunksCreated });
      }
      done++;
      if (total > 1) toast.loading(`Uploading ${done}/${total} files…`, { id: toastId });
    };

    // Upload a few files at a time so large archives don't take forever.
    const queue = [...pending];
    const workers = Array.from({ length: Math.min(4, queue.length) }, async () => {
      while (queue.length) {
        const next = queue.shift();
        if (next) await uploadOne(next);
      }
    });
    await Promise.all(workers);

    setIsUploading(false);
    if (total > 1) toast.dismiss(toastId);
    if (ok > 0) {
      toast.success(`${ok} files uploaded to the agent knowledge base`);
      await loadDocuments();
      // Clear the successfully-uploaded items after a short beat.
      setStaged((prev) => prev.filter((s) => s.status !== "done"));
    }
    if (ok < total) toast.error(`${total - ok} files failed`);
  };

  // ZIP archives are uploaded automatically — the extracted files land in the
  // knowledge base without an extra click.
  useEffect(() => {
    if (!autoUpload || isUploading) return;
    if (!staged.some((s) => s.status === "idle")) return;
    setAutoUpload(false);
    void uploadAll();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [autoUpload, staged, isUploading]);


  const handleDelete = async (doc: KnowledgeDoc) => {
    const { error } = await deleteKnowledgeDocument(doc.id);
    if (error) {
      toast.error("Failed to delete the file");
      return;
    }
    toast.success("File deleted");
    loadDocuments();
  };

  const [reprocessingId, setReprocessingId] = useState<string | null>(null);
  const [downloadingId, setDownloadingId] = useState<string | null>(null);

  const handleDownload = async (doc: KnowledgeDoc) => {
    setDownloadingId(doc.id);
    const { url, error } = await getKnowledgeDownloadUrl(doc);
    setDownloadingId(null);
    if (error || !url) {
      toast.error(error?.message || "Could not download the file");
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
    toast.success(`${label}: ${ok}/${docs.length} succeeded${ok < docs.length ? `, ${docs.length - ok} still failed` : ""}`,
      { duration: 8000 });
    loadDocuments();
  };

  // Re-run the ingest pipeline (PII scrub + distill + auto-classify) on every
  // document currently listed — used to clean up files uploaded before distillation.
  const handleReprocessAll = () => runBatch([...documents], "Reprocessed");

  // Retry only the documents that ended in "error".
  const handleRetryFailed = () => runBatch(documents.filter((d) => d.status === "error"), "Retried");

  const handleSync = async () => {
    setIsSyncing(true);
    toast.info("Syncing from SharePoint and Zoho CRM...");
    const { data, error } = await triggerKnowledgeSync();
    setIsSyncing(false);
    if (error) {
      toast.error(`Sync error: ${error.message}`, { duration: 10000 });
    } else {
      const sums = (data?.summaries || []) as Array<any>;
      const totals = sums.reduce(
        (a, s) => ({ created: a.created + (s.created || 0), updated: a.updated + (s.updated || 0), errors: a.errors + (s.errors || 0) }),
        { created: 0, updated: 0, errors: 0 },
      );
      toast.success(`Sync complete: ${totals.created} new, ${totals.updated} updated${totals.errors ? `, ${totals.errors} errors` : ""}`);
      // Surface per-source problems (e.g. "not configured") so setup gaps are visible.
      sums.filter((s) => !s.ok || s.message).forEach((s) => {
        if (s.message) toast.warning(`${s.source}: ${s.message}`, { duration: 12000 });
      });
    }
    loadDocuments();
  };

  const handleReprocess = async (doc: KnowledgeDoc) => {
    setReprocessingId(doc.id);
    toast.info(`Reprocessing ${doc.file_name}...`);
    const { error } = await processKnowledgeDocument(doc.id);
    setReprocessingId(null);
    if (error) {
      toast.error(`Processing error: ${error.message}`, { duration: 10000 });
    } else {
      toast.success("Processing complete");
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
        return <Badge className="bg-green-600 hover:bg-green-600">Ready</Badge>;
      case "processing":
        return (
          <Badge variant="secondary">
            <Loader2 className="h-3 w-3 animate-spin ml-1" />
            Processing
          </Badge>
        );
      case "pending":
        return <Badge variant="outline">Pending</Badge>;
      case "error":
        return <Badge variant="destructive">Error</Badge>;
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
            <CardTitle className="text-lg">Upload files to the knowledge base</CardTitle>
          </div>
          <p className="text-sm text-muted-foreground">
            Upload as many files as you like. For each file pick a <strong>domain</strong> and <strong>type</strong>, and add
            a short description (optional) telling the agent when to use it. Files are indexed into the agent RAG automatically.
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
            <p className="text-sm mb-3">Drag files here or pick from your computer</p>
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
              Choose files
            </Button>
            <p className="text-xs text-muted-foreground mt-3">
              PDF, Word, Excel, CSV, text, Markdown, JSON, XML, ZIP · up to 25MB per file
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
                      <Label className="text-xs">Domain</Label>
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
                      <Label className="text-xs">Type</Label>
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
                    <Label className="text-xs">Short description for the agent (optional)</Label>
                    <Input
                      placeholder="e.g. Overseas travel expense reimbursement policy 2026"
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
                  Clear all
                </Button>
                <Button onClick={uploadAll} disabled={isUploading || pendingCount === 0}>
                  {isUploading ? (
                    <Loader2 className="h-4 w-4 animate-spin ml-2" />
                  ) : (
                    <Upload className="h-4 w-4 ml-2" />
                  )}
                  Upload {pendingCount > 0 ? `(${pendingCount})` : ""}
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
            <CardTitle className="text-lg">Knowledge base files ({documents.length})</CardTitle>
            <div className="flex items-center gap-2">
              {documents.some((d) => d.status === "error") && (
                <Button variant="outline" size="sm" className="gap-2 text-amber-700 border-amber-300" onClick={handleRetryFailed} disabled={!!bulk}>
                  <RefreshCw className={`h-4 w-4 ${bulk ? "animate-spin" : ""}`} />
                  {bulk ? `Retrying ${bulk.done}/${bulk.total}...` : `Retry failed (${documents.filter((d) => d.status === "error").length})`}
                </Button>
              )}
              {documents.length > 0 && (
                <Button variant="outline" size="sm" className="gap-2" onClick={handleReprocessAll} disabled={!!bulk}>
                  <RefreshCw className={`h-4 w-4 ${bulk ? "animate-spin" : ""}`} />
                  {bulk ? `Reprocessing ${bulk.done}/${bulk.total}...` : "Clean & reprocess all"}
                </Button>
              )}
              {audience === "internal" && (
                <Button variant="outline" size="sm" className="gap-2" onClick={handleSync} disabled={isSyncing}>
                  <CloudDownload className={`h-4 w-4 ${isSyncing ? "animate-pulse" : ""}`} />
                  {isSyncing ? "Syncing..." : "Sync from SharePoint / Zoho"}
                </Button>
              )}
            </div>
          </div>
        </CardHeader>
        <CardContent>
          {documents.length === 0 ? (
            <div className="text-center py-8 text-muted-foreground">
              <Brain className="h-12 w-12 mx-auto mb-3 opacity-30" />
              <p>No files in the knowledge base yet</p>
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
                      title="Download file"
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
                      title="Reprocess"
                      disabled={reprocessingId === doc.id}
                      onClick={() => handleReprocess(doc)}
                    >
                      <RefreshCw className={`h-4 w-4 ${reprocessingId === doc.id ? "animate-spin" : ""}`} />
                    </Button>
                    <Button
                      variant="ghost"
                      size="icon"
                      className="h-8 w-8 text-destructive hover:text-destructive"
                      title="Delete"
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
