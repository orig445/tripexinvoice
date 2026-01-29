import { useState, useCallback } from "react";
import { Upload, FileImage, Loader2, CheckCircle2, AlertCircle, Camera } from "lucide-react";
import { useIsMobile } from "@/hooks/use-mobile";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { cn } from "@/lib/utils";
import { supabase } from "@/integrations/supabase/client";
import { toast } from "sonner";

interface InvoiceUploaderProps {
  onInvoiceProcessed: (invoice: any) => void;
}

export function InvoiceUploader({ onInvoiceProcessed }: InvoiceUploaderProps) {
  const [isDragging, setIsDragging] = useState(false);
  const [isProcessing, setIsProcessing] = useState(false);
  const [previewUrl, setPreviewUrl] = useState<string | null>(null);
  const [status, setStatus] = useState<"idle" | "uploading" | "analyzing" | "success" | "error">("idle");
  const isMobile = useIsMobile();

  const handleDragOver = useCallback((e: React.DragEvent) => {
    e.preventDefault();
    setIsDragging(true);
  }, []);

  const handleDragLeave = useCallback((e: React.DragEvent) => {
    e.preventDefault();
    setIsDragging(false);
  }, []);

  const processFile = async (file: File) => {
    if (!file.type.startsWith("image/")) {
      toast.error("נא להעלות קובץ תמונה בלבד");
      return;
    }

    setIsProcessing(true);
    setStatus("uploading");

    try {
      // Create preview
      const previewUrl = URL.createObjectURL(file);
      setPreviewUrl(previewUrl);

      // Convert to base64
      const reader = new FileReader();
      const base64Promise = new Promise<string>((resolve, reject) => {
        reader.onload = () => resolve(reader.result as string);
        reader.onerror = reject;
      });
      reader.readAsDataURL(file);
      const base64 = await base64Promise;

      // Upload to storage
      const fileName = `${Date.now()}-${file.name}`;
      const { data: uploadData, error: uploadError } = await supabase.storage
        .from("invoices")
        .upload(fileName, file);

      if (uploadError) {
        console.error("Upload error:", uploadError);
        throw new Error("שגיאה בהעלאת הקובץ");
      }

      // Get public URL
      const { data: publicUrlData } = supabase.storage
        .from("invoices")
        .getPublicUrl(fileName);

      setStatus("analyzing");

      // Call AI to analyze
      const { data: analysisData, error: analysisError } = await supabase.functions.invoke(
        "analyze-invoice",
        {
          body: { imageBase64: base64 },
        }
      );

      if (analysisError || !analysisData?.success) {
        throw new Error(analysisData?.error || "שגיאה בניתוח החשבונית");
      }

      // Save to database
      const invoiceData = {
        ...analysisData.data,
        image_url: publicUrlData.publicUrl,
        raw_ai_response: analysisData.rawResponse,
        status: "processed",
      };

      const { data: savedInvoice, error: saveError } = await supabase
        .from("invoices")
        .insert(invoiceData)
        .select()
        .single();

      if (saveError) {
        console.error("Save error:", saveError);
        throw new Error("שגיאה בשמירת החשבונית");
      }

      setStatus("success");
      toast.success("החשבונית נותחה בהצלחה!");
      onInvoiceProcessed(savedInvoice);

      // Reset after delay
      setTimeout(() => {
        setStatus("idle");
        setPreviewUrl(null);
      }, 2000);
    } catch (error) {
      console.error("Error processing invoice:", error);
      setStatus("error");
      toast.error(error instanceof Error ? error.message : "שגיאה בעיבוד החשבונית");
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
                  <span className="text-muted-foreground">מעלה קובץ...</span>
                </>
              )}
              {status === "analyzing" && (
                <>
                  <Loader2 className="h-5 w-5 animate-spin text-primary" />
                  <span className="text-muted-foreground">מנתח חשבונית באמצעות AI...</span>
                </>
              )}
              {status === "success" && (
                <>
                  <CheckCircle2 className="h-5 w-5 text-triplex-success" />
                  <span className="text-triplex-success font-medium">הניתוח הושלם!</span>
                </>
              )}
              {status === "error" && (
                <>
                  <AlertCircle className="h-5 w-5 text-destructive" />
                  <span className="text-destructive">שגיאה בעיבוד</span>
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
              <h3 className="text-xl font-semibold mb-2">העלאת חשבונית</h3>
              <p className="text-muted-foreground mb-4">
                גרור קובץ לכאן או לחץ לבחירת קובץ
              </p>
            </div>

            <div className="flex flex-col sm:flex-row gap-3">
              {isMobile && (
                <label className="cursor-pointer">
                  <input
                    type="file"
                    accept="image/*"
                    capture="environment"
                    onChange={handleFileSelect}
                    className="hidden"
                    disabled={isProcessing}
                  />
                  <Button variant="default" size="lg" asChild disabled={isProcessing}>
                    <span className="gap-2">
                      <Camera className="h-5 w-5" />
                      צלם תמונה
                    </span>
                  </Button>
                </label>
              )}

              <label className="cursor-pointer">
                <input
                  type="file"
                  accept="image/*"
                  onChange={handleFileSelect}
                  className="hidden"
                  disabled={isProcessing}
                />
                <Button variant={isMobile ? "outline" : "default"} size="lg" asChild disabled={isProcessing}>
                  <span className="gap-2">
                    <FileImage className="h-5 w-5" />
                    בחר מגלריה
                  </span>
                </Button>
              </label>
            </div>

            <p className="text-sm text-muted-foreground mt-4">
              {isMobile ? "צלם או בחר תמונה מהמכשיר" : "תומך בפורמטים: JPG, PNG, WEBP"}
            </p>
          </>
        )}
      </div>
    </Card>
  );
}
