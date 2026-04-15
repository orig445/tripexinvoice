import { useState, useRef } from "react";
import { supabase } from "@/integrations/supabase/client";
import { analyzeInvoice } from "@/lib/api-service";
import { useAuth } from "@/hooks/useAuth";
import { pdfPageToImage } from "@/lib/pdf-utils";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Progress } from "@/components/ui/progress";
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
} from "lucide-react";

interface QueueItem {
  file: File;
  status: "pending" | "uploading" | "analyzing" | "saving" | "done" | "error";
  error?: string;
  result?: any;
}

export function BulkReceiptTraining() {
  const [queue, setQueue] = useState<QueueItem[]>([]);
  const [isProcessing, setIsProcessing] = useState(false);
  const inputRef = useRef<HTMLInputElement>(null);
  const { user } = useAuth();

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
      // 1. Convert to JPEG
      updateItem(index, { status: "uploading" });
      let base64: string;
      let blob: Blob;
      if (item.file.type === "application/pdf") {
        const result = await pdfPageToImage(item.file);
        base64 = result.base64DataUrl;
        blob = result.blob;
      } else {
        const result = await compressToJpeg(item.file);
        base64 = result.base64;
        blob = result.blob;
      }

      // Upload to storage
      const fileName = `${Date.now()}-${Math.random().toString(36).slice(2)}.jpg`;
      const { error: uploadError } = await supabase.storage
        .from("invoices")
        .upload(fileName, blob, { contentType: "image/jpeg" });
      if (uploadError) throw new Error(`Upload: ${uploadError.message}`);

      const { data: publicUrlData } = supabase.storage
        .from("invoices")
        .getPublicUrl(fileName);

      // 2. Analyze with OCR
      updateItem(index, { status: "analyzing" });
      const { data: analysisData, error: analysisError } = await analyzeInvoice(base64);
      if (analysisError || !analysisData?.success) {
        throw new Error(analysisData?.error || "OCR failed");
      }

      // 3. Save invoice
      updateItem(index, { status: "saving" });
      const raw = analysisData;
      const f = raw.fields || raw.Fields;
      const d = raw.data;

      let invoiceData: Record<string, any>;
      if (f) {
        invoiceData = {
          invoice_number: f.invoiceNumber || f.InvoiceNumber || null,
          invoice_date: f.invoiceDate || f.InvoiceDate || null,
          currency: f.currency || f.Currency || null,
          vendor_name: f.merchantName || f.MerchantName || null,
          vendor_id: f.merchantTin || f.MerchantTin || null,
          vendor_address:
            [f.merchantAddress || f.MerchantAddress, f.merchantCity || f.MerchantCity]
              .filter(Boolean)
              .join(", ") || null,
          total_amount: parseFloat(f.total || f.Total || f.totalAmount || f.TotalAmount || "0") || null,
          tax_amount: parseFloat(f.totalVAT || f.TotalVAT || "0") || null,
          subtotal: parseFloat(f.subCategory || f.SubCategory || "0") || null,
          image_url: publicUrlData.publicUrl,
          raw_ai_response: raw.rawResponse || raw.RawResponse || JSON.stringify(raw),
          status: "processed",
          user_id: user?.id,
        };
      } else if (d) {
        const totalAmount =
          (d.amounts?.vatable_sales_amount || 0) +
          (d.amounts?.non_vatable_sales_amount || 0) +
          (d.amounts?.service_charge_amount || 0) +
          (d.amounts?.tax_amount || 0);
        invoiceData = {
          invoice_number: d.invoice_number || null,
          invoice_date: d.invoice_date || null,
          currency: d.currency || null,
          vendor_name: d.merchant?.name || null,
          vendor_id: d.merchant?.tin || null,
          vendor_address:
            [d.merchant?.address, d.merchant?.city].filter(Boolean).join(", ") || null,
          total_amount: totalAmount || null,
          tax_amount: d.amounts?.tax_amount || null,
          subtotal: d.amounts?.vatable_sales_amount || null,
          image_url: publicUrlData.publicUrl,
          raw_ai_response: raw.rawResponse || JSON.stringify(raw),
          status: "processed",
          user_id: user?.id,
        };
      } else {
        throw new Error("Unexpected OCR response format");
      }

      const { data: savedInvoice, error: saveError } = await supabase
        .from("invoices")
        .insert(invoiceData)
        .select()
        .single();

      if (saveError) throw new Error(`Save: ${saveError.message}`);

      // 4. Save to knowledge base
      const knowledgeContent = [
        `Invoice #${savedInvoice.invoice_number || "N/A"}`,
        `Date: ${savedInvoice.invoice_date || "N/A"}`,
        `Vendor: ${savedInvoice.vendor_name || "N/A"}`,
        `Vendor ID/TIN: ${savedInvoice.vendor_id || "N/A"}`,
        `Address: ${savedInvoice.vendor_address || "N/A"}`,
        `Currency: ${savedInvoice.currency || "N/A"}`,
        `Subtotal: ${savedInvoice.subtotal ?? "N/A"}`,
        `Tax: ${savedInvoice.tax_amount ?? "N/A"}`,
        `Total: ${savedInvoice.total_amount ?? "N/A"}`,
        `Status: ${savedInvoice.status}`,
      ].join("\n");

      const { data: knowledgeDoc } = await supabase
        .from("knowledge_documents")
        .insert({
          file_name: `invoice-${savedInvoice.invoice_number || savedInvoice.id}.txt`,
          file_type: "text/plain",
          file_url: savedInvoice.image_url || "",
          file_size: knowledgeContent.length,
          uploaded_by: user?.id,
          status: "ready",
        })
        .select()
        .single();

      if (knowledgeDoc) {
        await supabase.from("knowledge_chunks").insert({
          document_id: knowledgeDoc.id,
          content: knowledgeContent,
          chunk_index: 0,
        });
      }

      updateItem(index, { status: "done", result: savedInvoice });
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
      // Small delay to avoid rate limiting
      await new Promise((r) => setTimeout(r, 1500));
    }

    setIsProcessing(false);
    toast.success("העיבוד המאסיבי הסתיים!");
  };

  const clearQueue = () => {
    setQueue([]);
  };

  const doneCount = queue.filter((i) => i.status === "done").length;
  const errorCount = queue.filter((i) => i.status === "error").length;
  const progress = queue.length > 0 ? ((doneCount + errorCount) / queue.length) * 100 : 0;

  const getStatusIcon = (status: QueueItem["status"]) => {
    switch (status) {
      case "pending":
        return <FileImage className="h-4 w-4 text-muted-foreground" />;
      case "uploading":
      case "analyzing":
      case "saving":
        return <Loader2 className="h-4 w-4 animate-spin text-primary" />;
      case "done":
        return <CheckCircle2 className="h-4 w-4 text-green-500" />;
      case "error":
        return <XCircle className="h-4 w-4 text-destructive" />;
    }
  };

  const getStatusText = (status: QueueItem["status"]) => {
    switch (status) {
      case "pending":
        return "ממתין";
      case "uploading":
        return "מעלה...";
      case "analyzing":
        return "מנתח OCR...";
      case "saving":
        return "שומר...";
      case "done":
        return "הושלם";
      case "error":
        return "שגיאה";
    }
  };

  return (
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
          העלה קבלות וחשבוניות בכמויות — המערכת תסרוק, תנתח ותלמד מכל אחת אוטומטית
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
            {/* Progress bar */}
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

            {/* Start button */}
            {!isProcessing && doneCount + errorCount < queue.length && (
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

            {/* File list */}
            <div className="max-h-80 overflow-y-auto space-y-1.5">
              {queue.map((item, i) => (
                <div
                  key={i}
                  className="flex items-center justify-between p-2.5 rounded-lg border bg-card text-sm"
                >
                  <div className="flex items-center gap-2 min-w-0">
                    {getStatusIcon(item.status)}
                    <span className="truncate max-w-[200px]">{item.file.name}</span>
                    <span className="text-xs text-muted-foreground">
                      ({(item.file.size / 1024).toFixed(0)} KB)
                    </span>
                  </div>
                  <div className="flex items-center gap-2">
                    {item.result?.vendor_name && (
                      <span className="text-xs text-muted-foreground hidden sm:inline">
                        {item.result.vendor_name}
                      </span>
                    )}
                    <Badge
                      variant={
                        item.status === "done"
                          ? "default"
                          : item.status === "error"
                          ? "destructive"
                          : "secondary"
                      }
                      className={item.status === "done" ? "bg-green-600" : ""}
                    >
                      {getStatusText(item.status)}
                    </Badge>
                  </div>
                </div>
              ))}
            </div>
          </>
        )}
      </CardContent>
    </Card>
  );
}
