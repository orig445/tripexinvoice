import { useState, useCallback } from "react";
import { Upload, FileImage, Loader2, CheckCircle2, AlertCircle, Camera, FileText } from "lucide-react";
import { useIsMobile } from "@/hooks/use-mobile";
import { useAuth } from "@/hooks/useAuth";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { cn } from "@/lib/utils";
import { supabase } from "@/integrations/supabase/client";
import { toast } from "sonner";
import { pdfPageToImage } from "@/lib/pdf-utils";
import { analyzeInvoice } from "@/lib/api-service";

interface InvoiceUploaderProps {
  onInvoiceProcessed: (invoice: any) => void;
}

export function InvoiceUploader({ onInvoiceProcessed }: InvoiceUploaderProps) {
  const [isDragging, setIsDragging] = useState(false);
  const [isProcessing, setIsProcessing] = useState(false);
  const [previewUrl, setPreviewUrl] = useState<string | null>(null);
  const [status, setStatus] = useState<"idle" | "uploading" | "analyzing" | "success" | "error">("idle");
  const isMobile = useIsMobile();
  const { user } = useAuth();

  const handleDragOver = useCallback((e: React.DragEvent) => {
    e.preventDefault();
    setIsDragging(true);
  }, []);

  const handleDragLeave = useCallback((e: React.DragEvent) => {
    e.preventDefault();
    setIsDragging(false);
  }, []);

  // Compress aggressively: target ~800KB JPEG so OCI Gemini doesn't time out on QA.
  // Iteratively lowers quality if the result is still too large (e.g. iPhone HEIC->JPEG ~5MB).
  const compressToJpeg = (file: File, maxWidth = 1400, initialQuality = 0.75, targetBytes = 800_000): Promise<{ base64: string; blob: Blob }> => {
    return new Promise((resolve, reject) => {
      const img = new Image();
      const url = URL.createObjectURL(file);
      img.onload = () => {
        URL.revokeObjectURL(url);
        const scale = Math.min(1, maxWidth / img.width);
        const canvas = document.createElement("canvas");
        canvas.width = Math.round(img.width * scale);
        canvas.height = Math.round(img.height * scale);
        const ctx = canvas.getContext("2d");
        if (!ctx) return reject(new Error("Canvas not supported"));
        ctx.drawImage(img, 0, 0, canvas.width, canvas.height);

        const tryQuality = (q: number) => {
          canvas.toBlob(
            (blob) => {
              if (!blob) return reject(new Error("Failed to compress"));
              // If still too big and we can lower quality further, retry.
              if (blob.size > targetBytes && q > 0.4) {
                return tryQuality(Math.max(0.4, q - 0.1));
              }
              const base64 = canvas.toDataURL("image/jpeg", q);
              console.log(`[InvoiceUploader] Compressed to ${(blob.size / 1024).toFixed(0)}KB @ q=${q.toFixed(2)}, ${canvas.width}x${canvas.height}`);
              resolve({ base64, blob });
            },
            "image/jpeg",
            q
          );
        };
        tryQuality(initialQuality);
      };
      img.onerror = () => { URL.revokeObjectURL(url); reject(new Error("Failed to load image")); };
      img.src = url;
    });
  };

  const processFile = async (file: File) => {
    const isPdf = file.type === "application/pdf";
    if (!file.type.startsWith("image/") && !isPdf) {
      toast.error("Please upload an image or PDF file");
      return;
    }

    setIsProcessing(true);
    setStatus("uploading");

    try {
      // Convert to JPEG (handles HEIC, PDF and other formats)
      let base64: string;
      let blob: Blob;
      if (isPdf) {
        const result = await pdfPageToImage(file);
        base64 = result.base64DataUrl;
        blob = result.blob;
      } else {
        const result = await compressToJpeg(file);
        base64 = result.base64;
        blob = result.blob;
      }
      const previewUrl = URL.createObjectURL(blob);
      setPreviewUrl(previewUrl);

      const fileName = `${Date.now()}-image.jpg`;
      const { data: uploadData, error: uploadError } = await supabase.storage
        .from("invoices")
        .upload(fileName, blob, { contentType: "image/jpeg" });

      if (uploadError) {
        console.error("Upload error:", uploadError);
        throw new Error("Error uploading file");
      }

      const { data: publicUrlData } = supabase.storage
        .from("invoices")
        .getPublicUrl(fileName);

      setStatus("analyzing");

      const { data: analysisData, error: analysisError } = await analyzeInvoice(base64);

      if (analysisError || !analysisData?.success) {
        throw new Error(analysisData?.error || "Error analyzing invoice");
      }

      // Map response — support both flat fields format (C# backend) and nested format (Supabase)
      const raw = analysisData;
      const f = raw.fields || raw.Fields; // C# backend returns flat InvoiceFields
      const d = raw.data; // Legacy nested format
      
      let invoiceData: Record<string, any>;
      
      if (f) {
        // Flat fields format from C# backend
        invoiceData = {
          invoice_number: f.invoiceNumber || f.InvoiceNumber || null,
          invoice_date: f.invoiceDate || f.InvoiceDate || null,
          currency: f.currency || f.Currency || null,
          vendor_name: f.merchantName || f.MerchantName || null,
          vendor_id: f.merchantTin || f.MerchantTin || null,
          vendor_address: [f.merchantAddress || f.MerchantAddress, f.merchantCity || f.MerchantCity].filter(Boolean).join(", ") || null,
          total_amount: parseFloat(f.total || f.Total || f.totalAmount || f.TotalAmount || "0") || null,
          tax_amount: parseFloat(f.totalVAT || f.TotalVAT || "0") || null,
          subtotal: parseFloat(f.subCategory || f.SubCategory || "0") || null,
          image_url: publicUrlData.publicUrl,
          raw_ai_response: raw.rawResponse || raw.RawResponse || JSON.stringify(raw),
          status: "processed",
          user_id: user?.id,
        };
      } else if (d) {
        // Legacy nested format (Supabase edge functions)
        const totalAmount = (d.amounts?.vatable_sales_amount || 0) + (d.amounts?.non_vatable_sales_amount || 0) + (d.amounts?.service_charge_amount || 0) + (d.amounts?.tax_amount || 0);
        invoiceData = {
          invoice_number: d.invoice_number || null,
          invoice_date: d.invoice_date || null,
          currency: d.currency || null,
          vendor_name: d.merchant?.name || null,
          vendor_id: d.merchant?.tin || null,
          vendor_address: [d.merchant?.address, d.merchant?.city].filter(Boolean).join(", ") || null,
          total_amount: totalAmount || null,
          tax_amount: d.amounts?.tax_amount || null,
          subtotal: d.amounts?.vatable_sales_amount || null,
          image_url: publicUrlData.publicUrl,
          raw_ai_response: raw.rawResponse || JSON.stringify(raw),
          status: "processed",
          user_id: user?.id,
        };
      } else {
        throw new Error("Unexpected response format from OCR");
      }

      const { data: savedInvoice, error: saveError } = await supabase
        .from("invoices")
        .insert(invoiceData)
        .select()
        .single();

      if (saveError) {
        console.error("Save error:", saveError);
        throw new Error("Error saving invoice");
      }

      setStatus("success");
      toast.success("Invoice analyzed successfully!");
      onInvoiceProcessed(savedInvoice);

      // Save to knowledge base for model learning (async, non-blocking)
      try {
        const knowledgeContent = [
          `Invoice #${savedInvoice.invoice_number || 'N/A'}`,
          `Date: ${savedInvoice.invoice_date || 'N/A'}`,
          `Vendor: ${savedInvoice.vendor_name || 'N/A'}`,
          `Vendor ID/TIN: ${savedInvoice.vendor_id || 'N/A'}`,
          `Address: ${savedInvoice.vendor_address || 'N/A'}`,
          `Currency: ${savedInvoice.currency || 'N/A'}`,
          `Subtotal: ${savedInvoice.subtotal ?? 'N/A'}`,
          `Tax: ${savedInvoice.tax_amount ?? 'N/A'}`,
          `Total: ${savedInvoice.total_amount ?? 'N/A'}`,
          `Status: ${savedInvoice.status}`,
        ].join('\n');

        // Create knowledge document entry
        const { data: knowledgeDoc, error: knowledgeDocError } = await supabase
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

        if (!knowledgeDocError && knowledgeDoc) {
          await supabase.from("knowledge_chunks").insert({
            document_id: knowledgeDoc.id,
            content: knowledgeContent,
            chunk_index: 0,
          });
          console.log("Invoice saved to knowledge base for learning");
        }
      } catch (kbError) {
        console.error("Failed to save to knowledge base (non-critical):", kbError);
      }

      setTimeout(() => {
        setStatus("idle");
        setPreviewUrl(null);
      }, 2000);
    } catch (error) {
      console.error("Error processing invoice:", error);
      setStatus("error");
      toast.error(error instanceof Error ? error.message : "Error processing invoice");
      setTimeout(() => setStatus("idle"), 3000);
    } finally {
      setIsProcessing(false);
    }
  };

  const handleDrop = useCallback((e: React.DragEvent) => {
    e.preventDefault();
    setIsDragging(false);
    const file = e.dataTransfer.files[0];
    if (file) processFile(file);
  }, []);

  const handleFileSelect = (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    if (file) processFile(file);
  };

  return (
    <Card
      className={cn(
        "relative overflow-hidden border-2 border-dashed transition-all duration-300",
        isDragging && "border-primary bg-primary/5 scale-[1.02]",
        status === "success" && "border-triplex-success bg-triplex-success/5",
        status === "error" && "border-destructive bg-destructive/5",
        !isDragging && status === "idle" && "border-muted-foreground/25 hover:border-primary/50"
      )}
      onDragOver={handleDragOver}
      onDragLeave={handleDragLeave}
      onDrop={handleDrop}
    >
      <div className="p-8 md:p-12 flex flex-col items-center justify-center text-center min-h-[300px]">
        {previewUrl && status !== "idle" ? (
          <div className="relative animate-fade-in">
            <img
              src={previewUrl}
              alt="Preview"
              className="max-h-48 rounded-lg shadow-lg mb-4 object-contain"
            />
            <div className="flex items-center gap-2 justify-center">
              {status === "uploading" && (
                <>
                  <Loader2 className="h-5 w-5 animate-spin text-primary" />
                  <span className="text-muted-foreground">Uploading file...</span>
                </>
              )}
              {status === "analyzing" && (
                <>
                  <Loader2 className="h-5 w-5 animate-spin text-primary" />
                  <span className="text-muted-foreground">Analyzing invoice with AI...</span>
                </>
              )}
              {status === "success" && (
                <>
                  <CheckCircle2 className="h-5 w-5 text-triplex-success" />
                  <span className="text-triplex-success font-medium">Analysis complete!</span>
                </>
              )}
              {status === "error" && (
                <>
                  <AlertCircle className="h-5 w-5 text-destructive" />
                  <span className="text-destructive">Processing error</span>
                </>
              )}
            </div>
          </div>
        ) : (
          <>
            <div className="mb-6">
              <div className="w-20 h-20 rounded-2xl bg-gradient-to-br from-primary/20 to-primary/5 flex items-center justify-center mb-4 mx-auto animate-float">
                {isProcessing ? (
                  <Loader2 className="h-10 w-10 text-primary animate-spin" />
                ) : (
                  <Upload className="h-10 w-10 text-primary" />
                )}
              </div>
              <h3 className="text-xl font-semibold mb-2">Upload Invoice</h3>
              <p className="text-muted-foreground mb-4">
                Drag a file here or click to select a file
              </p>
            </div>

            <div className="flex flex-col sm:flex-row gap-3">
              {isMobile && (
                <label className="cursor-pointer">
                  <input
                    type="file"
                accept="image/*,application/pdf"
                capture="environment"
                onChange={handleFileSelect}
                className="hidden"
                disabled={isProcessing}
                  />
                  <Button variant="default" size="lg" asChild disabled={isProcessing}>
                    <span className="gap-2">
                      <Camera className="h-5 w-5" />
                      Take Photo
                    </span>
                  </Button>
                </label>
              )}

              <label className="cursor-pointer">
                <input
                  type="file"
                accept="image/*,application/pdf"
                onChange={handleFileSelect}
                className="hidden"
                disabled={isProcessing}
                />
                <Button variant={isMobile ? "outline" : "default"} size="lg" asChild disabled={isProcessing}>
                  <span className="gap-2">
                    <FileImage className="h-5 w-5" />
                    Choose from Gallery
                  </span>
                </Button>
              </label>
            </div>

            <p className="text-sm text-muted-foreground mt-4">
              {isMobile ? "Take or select a photo/PDF from your device" : "Supports: JPG, PNG, WEBP, PDF"}
            </p>
          </>
        )}
      </div>
    </Card>
  );
}
