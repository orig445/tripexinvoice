import { useState, useRef, useEffect } from "react";
import { bulkTrainInvoice, verifyTrainingSample, rebuildOcrPatterns, getTrainingStats } from "@/lib/api-service";
import { useAuth } from "@/hooks/useAuth";
import { pdfPageToImage } from "@/lib/pdf-utils";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Progress } from "@/components/ui/progress";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { toast } from "sonner";
import {
  Upload,
  FolderUp,
  CheckCircle2,
  XCircle,
  Loader2,
  FileImage,
  Zap,
  Trash2,
  RefreshCw,
  ThumbsUp,
  ThumbsDown,
  Brain,
  BarChart3,
  Eye,
  X,
  ChevronLeft,
  ChevronRight,
  Edit3,
  Save,
} from "lucide-react";

interface QueueItem {
  file: File;
  status: "pending" | "uploading" | "analyzing" | "done" | "error" | "verified" | "rejected";
  error?: string;
  result?: any;
  sampleId?: string;
  previewUrl?: string; // base64 image for review
}

interface TrainingStats {
  totalSamples: number;
  verifiedSamples: number;
  rejectedSamples: number;
  patternsLearned: number;
  patterns: Array<{
    fieldName: string;
    rule: string;
    country?: string;
    confidence: number;
    sourceCount: number;
  }>;
}

// Flatten nested OCR data for display
function flattenFields(data: any): Array<{ key: string; label: string; value: string }> {
  if (!data) return [];
  const fields: Array<{ key: string; label: string; value: string }> = [];

  const add = (key: string, label: string, val: any) => {
    if (val != null && val !== "") fields.push({ key, label, value: String(val) });
  };

  add("document_type", "Document Type", data.document_type);
  add("invoice_number", "Invoice / Receipt #", data.invoice_number);
  add("invoice_date", "Date", data.invoice_date);
  add("currency", "Currency", data.currency);

  if (data.merchant) {
    add("merchant.name", "Merchant Name", data.merchant.name);
    add("merchant.tin", "TIN", data.merchant.tin);
    add("merchant.address", "Address", data.merchant.address);
    add("merchant.city", "City", data.merchant.city);
  }

  if (data.amounts) {
    add("amounts.vatable_sales_amount", "Vatable Sales", data.amounts.vatable_sales_amount);
    add("amounts.non_vatable_sales_amount", "Non-Vatable Sales", data.amounts.non_vatable_sales_amount);
    add("amounts.service_charge_amount", "Service Charge", data.amounts.service_charge_amount);
    add("amounts.tax_amount", "Tax / VAT", data.amounts.tax_amount);
  }

  if (data.payment) {
    add("payment.method", "Payment Method", data.payment.method);
    add("payment.form_of_payment", "Form of Payment", data.payment.form_of_payment);
    add("payment.amount_paid", "Amount Paid", data.payment.amount_paid);
    add("payment.card_last4", "Card Last 4", data.payment.card_last4);
    add("payment.card_type", "Card Type", data.payment.card_type);
  }

  return fields;
}

export function BulkReceiptTraining() {
  const [queue, setQueue] = useState<QueueItem[]>([]);
  const [isProcessing, setIsProcessing] = useState(false);
  const [isRebuilding, setIsRebuilding] = useState(false);
  const [stats, setStats] = useState<TrainingStats | null>(null);
  const [reviewIndex, setReviewIndex] = useState<number | null>(null);
  const [editMode, setEditMode] = useState(false);
  const [editedFields, setEditedFields] = useState<Record<string, string>>({});
  const inputRef = useRef<HTMLInputElement>(null);
  const { user } = useAuth();

  useEffect(() => {
    loadStats();
  }, []);

  const loadStats = async () => {
    try {
      const { data } = await getTrainingStats();
      if (data) setStats(data);
    } catch { /* ignore */ }
  };

  const handleFilesSelected = (e: React.ChangeEvent<HTMLInputElement>) => {
    const files = e.target.files;
    if (!files || files.length === 0) return;

    const newItems: QueueItem[] = Array.from(files)
      .filter((f) => f.type.startsWith("image/") || f.type === "application/pdf")
      .map((file) => ({ file, status: "pending" as const }));

    if (newItems.length === 0) {
      toast.error("לא נמצאו קבצי תמונה או PDF");
      return;
    }

    setQueue((prev) => [...prev, ...newItems]);
    toast.success(`${newItems.length} קבצים נוספו לתור`);
    e.target.value = "";
  };

  const compressToJpeg = (
    file: File,
    maxWidth = 1600,
    quality = 0.8
  ): Promise<{ base64: string; blob: Blob }> => {
    return new Promise((resolve, reject) => {
      const img = new Image();
      const url = URL.createObjectURL(file);
      img.onload = () => {
        URL.revokeObjectURL(url);
        const scale = Math.min(1, maxWidth / img.width);
        const canvas = document.createElement("canvas");
        canvas.width = img.width * scale;
        canvas.height = img.height * scale;
        const ctx = canvas.getContext("2d");
        if (!ctx) return reject(new Error("Canvas not supported"));
        ctx.drawImage(img, 0, 0, canvas.width, canvas.height);
        const base64 = canvas.toDataURL("image/jpeg", quality);
        canvas.toBlob(
          (blob) => {
            if (blob) resolve({ base64, blob });
            else reject(new Error("Failed to compress"));
          },
          "image/jpeg",
          quality
        );
      };
      img.onerror = () => {
        URL.revokeObjectURL(url);
        reject(new Error("Failed to load image"));
      };
      img.src = url;
    });
  };

  const updateItem = (index: number, update: Partial<QueueItem>) => {
    setQueue((prev) =>
      prev.map((item, i) => (i === index ? { ...item, ...update } : item))
    );
  };

  const processOne = async (item: QueueItem, index: number) => {
    try {
      updateItem(index, { status: "uploading" });
      let base64: string;
      if (item.file.type === "application/pdf") {
        const result = await pdfPageToImage(item.file);
        base64 = result.base64DataUrl;
      } else {
        const result = await compressToJpeg(item.file);
        base64 = result.base64;
      }

      updateItem(index, { status: "analyzing", previewUrl: base64 });
      const { data, error } = await bulkTrainInvoice(base64, "IL");

      if (error || !data?.success) {
        throw new Error(data?.error || error?.message || "OCR failed");
      }

      updateItem(index, {
        status: "done",
        result: data.fields || data.Fields,
        sampleId: data.sampleId || data.SampleId,
        previewUrl: base64,
      });
    } catch (err: any) {
      console.error(`Error processing ${item.file.name}:`, err);
      updateItem(index, { status: "error", error: err.message });
    }
  };

  const startProcessing = async () => {
    if (isProcessing) return;
    setIsProcessing(true);

    const pendingIndexes = queue
      .map((item, i) => (item.status === "pending" || item.status === "error" ? i : -1))
      .filter((i) => i !== -1);

    for (const idx of pendingIndexes) {
      await processOne(queue[idx], idx);
      await new Promise((r) => setTimeout(r, 1500));
    }

    setIsProcessing(false);
    toast.success("העיבוד המאסיבי הסתיים!");
    loadStats();
  };

  const handleVerify = async (index: number, isCorrect: boolean) => {
    const item = queue[index];
    if (!item.sampleId) return;

    try {
      const corrections = !isCorrect && Object.keys(editedFields).length > 0 ? editedFields : undefined;
      await verifyTrainingSample(item.sampleId, isCorrect, corrections);
      updateItem(index, { status: isCorrect ? "verified" : "rejected" });
      toast.success(isCorrect ? "✅ דוגמה אושרה" : "❌ דוגמה נדחתה");
      setEditMode(false);
      setEditedFields({});
      // Move to next pending review
      const nextIdx = queue.findIndex((q, i) => i > index && q.status === "done");
      if (nextIdx !== -1) {
        setReviewIndex(nextIdx);
      } else {
        setReviewIndex(null);
      }
    } catch (err: any) {
      toast.error(`שגיאה: ${err.message}`);
    }
  };

  const handleRebuildPatterns = async () => {
    setIsRebuilding(true);
    try {
      const { data, error } = await rebuildOcrPatterns();
      if (error || !data?.success) {
        toast.error(data?.error || "שגיאה בבניית דפוסים");
      } else {
        toast.success(`נבנו ${data.patternsCreated || data.PatternsCreated} דפוסים מ-${data.samplesAnalyzed || data.SamplesAnalyzed} דוגמאות`);
        loadStats();
      }
    } catch (err: any) {
      toast.error(err.message);
    }
    setIsRebuilding(false);
  };

  const openReview = (index: number) => {
    setReviewIndex(index);
    setEditMode(false);
    setEditedFields({});
  };

  const clearQueue = () => { setQueue([]); setReviewIndex(null); };

  const doneCount = queue.filter((i) => ["done", "verified", "rejected"].includes(i.status)).length;
  const errorCount = queue.filter((i) => i.status === "error").length;
  const progress = queue.length > 0 ? ((doneCount + errorCount) / queue.length) * 100 : 0;
  const pendingReviewCount = queue.filter((i) => i.status === "done").length;

  const getStatusIcon = (status: QueueItem["status"]) => {
    switch (status) {
      case "pending": return <FileImage className="h-4 w-4 text-muted-foreground" />;
      case "uploading":
      case "analyzing": return <Loader2 className="h-4 w-4 animate-spin text-primary" />;
      case "done": return <Eye className="h-4 w-4 text-amber-500" />;
      case "verified": return <ThumbsUp className="h-4 w-4 text-green-500" />;
      case "rejected": return <ThumbsDown className="h-4 w-4 text-red-500" />;
      case "error": return <XCircle className="h-4 w-4 text-destructive" />;
    }
  };

  const getStatusText = (status: QueueItem["status"]) => {
    switch (status) {
      case "pending": return "ממתין";
      case "uploading": return "מעלה...";
      case "analyzing": return "מנתח OCR...";
      case "done": return "ממתין לבדיקה";
      case "verified": return "אושר ✅";
      case "rejected": return "נדחה ❌";
      case "error": return "שגיאה";
    }
  };

  // Review panel for a single item
  const reviewItem = reviewIndex !== null ? queue[reviewIndex] : null;
  const reviewFields = reviewItem?.result ? flattenFields(reviewItem.result) : [];

  // Navigation between reviewable items
  const reviewableIndexes = queue
    .map((item, i) => (item.status === "done" ? i : -1))
    .filter((i) => i !== -1);
  const currentReviewPos = reviewIndex !== null ? reviewableIndexes.indexOf(reviewIndex) : -1;

  return (
    <div className="space-y-4">
      {/* ═══ Review Panel (overlay) ═══ */}
      {reviewItem && reviewIndex !== null && (
        <Card className="border-primary/50 shadow-lg">
          <CardHeader className="pb-3">
            <div className="flex items-center justify-between">
              <div className="flex items-center gap-2">
                <Eye className="h-5 w-5 text-primary" />
                <CardTitle className="text-lg">בדיקת קבלה — {reviewItem.file.name}</CardTitle>
              </div>
              <div className="flex items-center gap-2">
                {/* Navigation */}
                {reviewableIndexes.length > 1 && (
                  <div className="flex items-center gap-1 text-sm text-muted-foreground">
                    <Button
                      size="sm" variant="ghost" className="h-7 w-7 p-0"
                      disabled={currentReviewPos <= 0}
                      onClick={() => setReviewIndex(reviewableIndexes[currentReviewPos - 1])}
                    >
                      <ChevronRight className="h-4 w-4" />
                    </Button>
                    <span>{currentReviewPos + 1}/{reviewableIndexes.length}</span>
                    <Button
                      size="sm" variant="ghost" className="h-7 w-7 p-0"
                      disabled={currentReviewPos >= reviewableIndexes.length - 1}
                      onClick={() => setReviewIndex(reviewableIndexes[currentReviewPos + 1])}
                    >
                      <ChevronLeft className="h-4 w-4" />
                    </Button>
                  </div>
                )}
                <Button size="sm" variant="ghost" className="h-7 w-7 p-0" onClick={() => setReviewIndex(null)}>
                  <X className="h-4 w-4" />
                </Button>
              </div>
            </div>
          </CardHeader>
          <CardContent>
            <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
              {/* Left: Receipt Image */}
              <div className="border rounded-lg overflow-hidden bg-muted/30">
                {reviewItem.previewUrl ? (
                  <img
                    src={reviewItem.previewUrl}
                    alt="Receipt"
                    className="w-full h-auto max-h-[500px] object-contain"
                  />
                ) : (
                  <div className="flex items-center justify-center h-64 text-muted-foreground">
                    <FileImage className="h-12 w-12" />
                  </div>
                )}
              </div>

              {/* Right: Extracted Data */}
              <div className="space-y-3">
                <div className="flex items-center justify-between">
                  <h3 className="font-semibold text-sm">נתונים שחולצו</h3>
                  <Button
                    size="sm" variant="outline" className="gap-1 h-7 text-xs"
                    onClick={() => {
                      if (editMode) {
                        setEditMode(false);
                        setEditedFields({});
                      } else {
                        setEditMode(true);
                        // Pre-fill with current values
                        const initial: Record<string, string> = {};
                        reviewFields.forEach(f => { initial[f.key] = f.value; });
                        setEditedFields(initial);
                      }
                    }}
                  >
                    {editMode ? <X className="h-3 w-3" /> : <Edit3 className="h-3 w-3" />}
                    {editMode ? "בטל עריכה" : "ערוך תיקונים"}
                  </Button>
                </div>

                <div className="space-y-1.5 max-h-[400px] overflow-y-auto">
                  {reviewFields.length === 0 && (
                    <p className="text-sm text-muted-foreground">לא חולצו נתונים</p>
                  )}
                  {reviewFields.map((field) => (
                    <div key={field.key} className="flex items-center gap-2 text-sm">
                      <span className="text-muted-foreground min-w-[120px] text-xs font-medium">
                        {field.label}:
                      </span>
                      {editMode ? (
                        <Input
                          value={editedFields[field.key] ?? field.value}
                          onChange={(e) => setEditedFields(prev => ({ ...prev, [field.key]: e.target.value }))}
                          className="h-7 text-xs"
                        />
                      ) : (
                        <span className="font-mono text-xs bg-muted/50 px-1.5 py-0.5 rounded">
                          {field.value}
                        </span>
                      )}
                    </div>
                  ))}
                </div>

                {/* Action buttons */}
                <div className="flex gap-2 pt-3 border-t">
                  <Button
                    className="flex-1 gap-1.5 bg-green-600 hover:bg-green-700"
                    onClick={() => handleVerify(reviewIndex, true)}
                  >
                    <ThumbsUp className="h-4 w-4" />
                    {editMode ? "אשר עם תיקונים" : "אשר — הנתונים נכונים"}
                  </Button>
                  <Button
                    variant="destructive"
                    className="flex-1 gap-1.5"
                    onClick={() => handleVerify(reviewIndex, false)}
                  >
                    <ThumbsDown className="h-4 w-4" />
                    דחה — נתונים שגויים
                  </Button>
                </div>
                {editMode && (
                  <p className="text-xs text-muted-foreground text-center">
                    ✏️ תקן את השדות השגויים ולחץ "אשר עם תיקונים" — המערכת תלמד מהתיקונים
                  </p>
                )}
              </div>
            </div>
          </CardContent>
        </Card>
      )}

      {/* ═══ Pending review alert ═══ */}
      {!isProcessing && pendingReviewCount > 0 && reviewIndex === null && (
        <Card className="border-amber-500/50 bg-amber-500/5">
          <CardContent className="py-3 flex items-center justify-between">
            <div className="flex items-center gap-2">
              <Eye className="h-5 w-5 text-amber-500" />
              <span className="font-medium text-sm">
                {pendingReviewCount} קבלות ממתינות לבדיקה ואישור
              </span>
            </div>
            <Button
              size="sm"
              variant="outline"
              className="gap-1"
              onClick={() => {
                const firstPending = queue.findIndex(q => q.status === "done");
                if (firstPending !== -1) openReview(firstPending);
              }}
            >
              <Eye className="h-4 w-4" />
              התחל בדיקה
            </Button>
          </CardContent>
        </Card>
      )}

      {/* ═══ Stats Card ═══ */}
      {stats && (
        <Card>
          <CardHeader className="pb-3">
            <div className="flex items-center gap-2">
              <BarChart3 className="h-5 w-5 text-primary" />
              <CardTitle className="text-lg">סטטיסטיקות אימון</CardTitle>
            </div>
          </CardHeader>
          <CardContent>
            <div className="grid grid-cols-2 md:grid-cols-4 gap-4 mb-4">
              <div className="text-center">
                <div className="text-2xl font-bold">{stats.totalSamples}</div>
                <div className="text-xs text-muted-foreground">סה"כ דוגמאות</div>
              </div>
              <div className="text-center">
                <div className="text-2xl font-bold text-green-500">{stats.verifiedSamples}</div>
                <div className="text-xs text-muted-foreground">מאומתות</div>
              </div>
              <div className="text-center">
                <div className="text-2xl font-bold text-red-500">{stats.rejectedSamples}</div>
                <div className="text-xs text-muted-foreground">נדחו</div>
              </div>
              <div className="text-center">
                <div className="text-2xl font-bold text-primary">{stats.patternsLearned}</div>
                <div className="text-xs text-muted-foreground">דפוסים נלמדו</div>
              </div>
            </div>

            {stats.patterns.length > 0 && (
              <div className="space-y-1.5 max-h-40 overflow-y-auto">
                <div className="text-sm font-medium mb-1">דפוסים שנלמדו:</div>
                {stats.patterns.map((p, i) => (
                  <div key={i} className="text-xs p-2 rounded bg-muted/50 flex justify-between items-start gap-2">
                    <span className="flex-1">
                      <Badge variant="outline" className="mr-1 text-[10px]">{p.fieldName}</Badge>
                      {p.rule}
                    </span>
                    <span className="text-muted-foreground whitespace-nowrap">{p.confidence.toFixed(0)}%</span>
                  </div>
                ))}
              </div>
            )}

            <Button
              variant="outline"
              className="w-full mt-3 gap-2"
              onClick={handleRebuildPatterns}
              disabled={isRebuilding || (stats.verifiedSamples < 3)}
            >
              {isRebuilding ? <Loader2 className="h-4 w-4 animate-spin" /> : <Brain className="h-4 w-4" />}
              {isRebuilding ? "בונה דפוסים..." : "בנה דפוסים מחדש"}
            </Button>
            {stats.verifiedSamples < 3 && (
              <p className="text-xs text-muted-foreground text-center mt-1">
                נדרשות לפחות 3 דוגמאות מאומתות
              </p>
            )}
          </CardContent>
        </Card>
      )}

      {/* ═══ Upload & Process Card ═══ */}
      <Card>
        <CardHeader>
          <div className="flex items-center justify-between">
            <div className="flex items-center gap-2">
              <Zap className="h-5 w-5 text-primary" />
              <CardTitle className="text-lg">אימון OCR מאסיבי</CardTitle>
            </div>
            <div className="flex gap-2">
              {queue.length > 0 && (
                <Button variant="outline" size="sm" onClick={clearQueue} disabled={isProcessing}>
                  <Trash2 className="h-4 w-4 ml-1" />
                  נקה תור
                </Button>
              )}
              <input
                ref={inputRef}
                type="file"
                className="hidden"
                multiple
                accept="image/*,application/pdf"
                onChange={handleFilesSelected}
              />
              <Button
                variant="outline"
                onClick={() => inputRef.current?.click()}
                disabled={isProcessing}
              >
                <FolderUp className="h-4 w-4 ml-1" />
                בחר קבצים
              </Button>
            </div>
          </div>
          <p className="text-sm text-muted-foreground">
            העלה קבלות — המערכת תסרוק, תציג תוצאות לאימות, ותלמד דפוסים מהדוגמאות המאושרות
          </p>
        </CardHeader>
        <CardContent className="space-y-4">
          {queue.length === 0 ? (
            <div
              className="border-2 border-dashed rounded-xl p-12 text-center cursor-pointer hover:border-primary/50 transition-colors"
              onClick={() => inputRef.current?.click()}
            >
              <Upload className="h-12 w-12 mx-auto mb-3 text-muted-foreground/40" />
              <p className="text-muted-foreground font-medium">
                לחץ כאן או גרור קבצים לכאן
              </p>
              <p className="text-sm text-muted-foreground mt-1">
                תמונות (JPG, PNG, WEBP) ו-PDF — ללא הגבלת כמות
              </p>
            </div>
          ) : (
            <>
              <div className="space-y-2">
                <div className="flex justify-between text-sm">
                  <span>
                    {doneCount}/{queue.length} הושלמו
                    {errorCount > 0 && (
                      <span className="text-destructive mr-2">({errorCount} שגיאות)</span>
                    )}
                  </span>
                  <span>{Math.round(progress)}%</span>
                </div>
                <Progress value={progress} className="h-2" />
              </div>

              {!isProcessing && queue.some(i => i.status === "pending" || i.status === "error") && (
                <Button onClick={startProcessing} className="w-full gap-2">
                  <Zap className="h-4 w-4" />
                  התחל עיבוד ({queue.filter((i) => i.status === "pending" || i.status === "error").length} קבצים)
                </Button>
              )}

              {isProcessing && (
                <div className="flex items-center justify-center gap-2 py-2 text-primary">
                  <Loader2 className="h-5 w-5 animate-spin" />
                  <span className="font-medium">מעבד... אל תסגור את הדף</span>
                </div>
              )}

              <div className="max-h-96 overflow-y-auto space-y-1.5">
                {queue.map((item, i) => (
                  <div
                    key={i}
                    className={`flex items-center justify-between p-2.5 rounded-lg border bg-card text-sm cursor-pointer hover:bg-muted/30 transition-colors ${reviewIndex === i ? "ring-2 ring-primary" : ""}`}
                    onClick={() => item.status === "done" && openReview(i)}
                  >
                    <div className="flex items-center gap-2 min-w-0">
                      {getStatusIcon(item.status)}
                      {/* Thumbnail */}
                      {item.previewUrl && (
                        <img src={item.previewUrl} alt="" className="h-8 w-8 rounded object-cover border" />
                      )}
                      <span className="truncate max-w-[180px]">{item.file.name}</span>
                      <span className="text-xs text-muted-foreground">
                        ({(item.file.size / 1024).toFixed(0)} KB)
                      </span>
                    </div>
                    <div className="flex items-center gap-2">
                      {item.result?.merchant?.name && (
                        <span className="text-xs text-muted-foreground hidden sm:inline">
                          {item.result.merchant.name}
                        </span>
                      )}

                      {item.status === "done" && (
                        <Button
                          size="sm" variant="outline" className="h-7 gap-1 text-xs"
                          onClick={(e) => { e.stopPropagation(); openReview(i); }}
                        >
                          <Eye className="h-3 w-3" />
                          בדוק
                        </Button>
                      )}

                      <Badge
                        variant={
                          item.status === "done" ? "secondary" :
                          item.status === "verified" ? "default" :
                          item.status === "rejected" || item.status === "error" ? "destructive" :
                          "secondary"
                        }
                        className={item.status === "verified" ? "bg-green-600" : ""}
                      >
                        {getStatusText(item.status)}
                      </Badge>
                    </div>
                  </div>
                ))}
              </div>

              {/* Rebuild after processing */}
              {!isProcessing && doneCount > 0 && (
                <div className="flex gap-2">
                  <Button
                    variant="outline"
                    className="flex-1 gap-2"
                    onClick={() => loadStats()}
                  >
                    <RefreshCw className="h-4 w-4" />
                    רענן סטטיסטיקות
                  </Button>
                </div>
              )}
            </>
          )}
        </CardContent>
      </Card>
    </div>
  );
}
