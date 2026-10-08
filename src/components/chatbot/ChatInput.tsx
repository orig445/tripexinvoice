import { useState, useRef, useEffect } from "react";
import { Send, Camera, Paperclip, Mic, MicOff, X, FileText } from "lucide-react";
import { Button } from "@/components/ui/button";
import { pdfAllPagesToBase64 } from "@/lib/pdf-utils";
import { useToast } from "@/hooks/use-toast";
import type { AttachmentIntent } from "@/lib/api-service";

/** A file the user picked but hasn't sent yet. */
interface PendingAttachment {
  id: string;
  name: string;
  base64: string;
  isPdf: boolean;
}

interface ChatInputProps {
  /**
   * The turn the user sent: their words plus anything attached to it. Attachments used to
   * be impossible to send alongside a message — picking a file fired it off on its own, to
   * be scanned as a receipt — which is why a screenshot could never be a reference for a
   * question.
   */
  onSend: (message: string, options?: { images?: string[]; attachmentIntent?: AttachmentIntent }) => void;
  /** The camera button's path: scan this receipt, no questions asked. */
  onImageCapture: (base64: string) => void;
  isLoading: boolean;
}

export function ChatInput({ onSend, onImageCapture, isLoading }: ChatInputProps) {
  const [value, setValue] = useState("");
  const [attachments, setAttachments] = useState<PendingAttachment[]>([]);
  const [isRecording, setIsRecording] = useState(false);
  const inputRef = useRef<HTMLTextAreaElement>(null);
  const cameraRef = useRef<HTMLInputElement>(null);
  const fileRef = useRef<HTMLInputElement>(null);
  const recognitionRef = useRef<any>(null);
  const baseTextRef = useRef<string>("");
  const { toast } = useToast();

  const SpeechRecognition =
    typeof window !== "undefined"
      ? (window as any).SpeechRecognition || (window as any).webkitSpeechRecognition
      : null;
  const speechSupported = !!SpeechRecognition;

  useEffect(() => {
    inputRef.current?.focus();
    return () => {
      try {
        recognitionRef.current?.stop();
      } catch {}
    };
  }, []);

  // return focus to the box when the bot finishes replying
  useEffect(() => {
    if (!isLoading) inputRef.current?.focus();
  }, [isLoading]);

  const startRecording = () => {
    if (!SpeechRecognition) {
      toast({
        title: "Voice input not supported",
        description: "Try Chrome, Edge, or Safari.",
        variant: "destructive",
      });
      return;
    }
    try {
      const recognition = new SpeechRecognition();
      recognition.continuous = true;
      recognition.interimResults = true;
      recognition.lang = "en-US";

      baseTextRef.current = value ? value.trim() + " " : "";

      recognition.onresult = (event: any) => {
        let interim = "";
        let final = "";
        for (let i = event.resultIndex; i < event.results.length; i++) {
          const transcript = event.results[i][0].transcript;
          if (event.results[i].isFinal) final += transcript;
          else interim += transcript;
        }
        if (final) baseTextRef.current += final + " ";
        setValue((baseTextRef.current + interim).trimStart());
      };

      recognition.onerror = (event: any) => {
        if (event.error !== "aborted" && event.error !== "no-speech") {
          toast({
            title: "Microphone error",
            description: event.error,
            variant: "destructive",
          });
        }
        setIsRecording(false);
      };

      recognition.onend = () => setIsRecording(false);

      recognition.start();
      recognitionRef.current = recognition;
      setIsRecording(true);
    } catch (err) {
      console.error("Speech recognition error:", err);
      setIsRecording(false);
    }
  };

  const stopRecording = () => {
    try {
      recognitionRef.current?.stop();
    } catch {}
    setIsRecording(false);
  };

  const toggleRecording = () => {
    if (isRecording) stopRecording();
    else startRecording();
  };

  const handleSubmit = () => {
    // An attachment on its own is a turn too — the message is the file.
    if ((!value.trim() && attachments.length === 0) || isLoading) return;
    onSend(
      value.trim(),
      attachments.length > 0 ? { images: attachments.map((a) => a.base64) } : undefined,
    );
    setValue("");
    setAttachments([]);
    // keep focus in the box so the user can keep typing right away
    requestAnimationFrame(() => inputRef.current?.focus());
  };

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === "Enter" && !e.shiftKey) {
      e.preventDefault();
      handleSubmit();
    }
  };

  const compressImage = (file: File, maxWidth = 1200, quality = 0.7): Promise<string> => {
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
        const dataUrl = canvas.toDataURL("image/jpeg", quality);
        const base64 = dataUrl.split(",")[1];
        if (base64) resolve(base64);
        else reject(new Error("Failed to compress"));
      };
      img.onerror = () => { URL.revokeObjectURL(url); reject(new Error("Failed to load image")); };
      img.src = url;
    });
  };

  const MAX_FILES = 5;

  /** The payload the backend gets: first page for a PDF, a compressed JPEG otherwise. */
  const toBase64 = async (file: File): Promise<string | null> => {
    if (file.type === "application/pdf") {
      const pages = await pdfAllPagesToBase64(file);
      return pages[0] ?? null;
    }
    return await compressImage(file);
  };

  /** Pick the files out of the event, capped and with the input reset so the same file can be picked twice. */
  const takeFiles = (e: React.ChangeEvent<HTMLInputElement>, room: number): File[] => {
    const filesList = e.target.files;
    e.target.value = "";
    if (!filesList || filesList.length === 0) return [];
    if (filesList.length > room) {
      toast({
        title: `Maximum ${MAX_FILES} files`,
        description: room > 0
          ? `Only the first ${room} will be used.`
          : "Send the ones you've attached first.",
      });
    }
    return Array.from(filesList).slice(0, room);
  };

  /** Camera: scan each receipt straight away, one request per file — unchanged behaviour. */
  const handleScanFiles = async (e: React.ChangeEvent<HTMLInputElement>) => {
    for (const file of takeFiles(e, MAX_FILES)) {
      try {
        const base64 = await toBase64(file);
        if (base64) onImageCapture(base64);
      } catch (err) {
        console.error("File processing error:", err);
        toast({ title: "Couldn't read that file", description: file.name, variant: "destructive" });
      }
    }
  };

  /**
   * Paperclip: attach to the message being written instead of sending it. The user can then
   * type what they want to know about it, and the server decides whether it is a receipt to
   * scan or something to look at while answering.
   */
  const handleAttachFiles = async (e: React.ChangeEvent<HTMLInputElement>) => {
    for (const file of takeFiles(e, MAX_FILES - attachments.length)) {
      try {
        const base64 = await toBase64(file);
        if (!base64) continue;
        setAttachments((prev) =>
          prev.length >= MAX_FILES
            ? prev
            : [...prev, { id: crypto.randomUUID(), name: file.name, base64, isPdf: file.type === "application/pdf" }],
        );
      } catch (err) {
        console.error("File processing error:", err);
        toast({ title: "Couldn't read that file", description: file.name, variant: "destructive" });
      }
    }
    inputRef.current?.focus();
  };

  const removeAttachment = (id: string) =>
    setAttachments((prev) => prev.filter((a) => a.id !== id));

  return (
    <div className="border-t bg-background">
      {/* Files waiting to go with the message being typed. */}
      {attachments.length > 0 && (
        <div className="flex flex-wrap gap-1.5 px-3 pt-3">
          {attachments.map((a) => (
            <span
              key={a.id}
              className="inline-flex max-w-[200px] items-center gap-1.5 rounded-lg border bg-muted/60 px-2 py-1 text-xs"
            >
              <FileText className="h-3.5 w-3.5 flex-shrink-0 text-muted-foreground" />
              <span className="truncate" title={a.name}>{a.name || (a.isPdf ? "document.pdf" : "image")}</span>
              <button
                type="button"
                className="flex-shrink-0 text-muted-foreground hover:text-destructive"
                onClick={() => removeAttachment(a.id)}
                aria-label={`Remove ${a.name}`}
                title="Remove"
              >
                <X className="h-3.5 w-3.5" />
              </button>
            </span>
          ))}
          <span className="self-center text-xs text-muted-foreground">
            Ask a question about {attachments.length === 1 ? "it" : "them"}, or send as is to scan.
          </span>
        </div>
      )}

      <div className="flex items-end gap-1.5 p-3">
      <Button
        type="button"
        variant="ghost"
        size="icon"
        className="h-9 w-9 flex-shrink-0 text-muted-foreground hover:text-primary"
        onClick={() => cameraRef.current?.click()}
        disabled={isLoading}
        title="Scan an invoice"
      >
        <Camera className="h-4 w-4" />
      </Button>
      <input
        ref={cameraRef}
        type="file"
        accept="image/*,application/pdf"
        capture="environment"
        multiple
        className="hidden"
        onChange={handleScanFiles}
      />

      <Button
        type="button"
        variant="ghost"
        size="icon"
        className="h-9 w-9 flex-shrink-0 text-muted-foreground hover:text-primary"
        onClick={() => fileRef.current?.click()}
        disabled={isLoading || attachments.length >= MAX_FILES}
        title="Attach a file to your message (up to 5)"
      >
        <Paperclip className="h-4 w-4" />
      </Button>
      <input
        ref={fileRef}
        type="file"
        accept="image/*,application/pdf"
        multiple
        className="hidden"
        onChange={handleAttachFiles}
      />

      {speechSupported && (
        <Button
          type="button"
          variant={isRecording ? "default" : "ghost"}
          size="icon"
          className={`h-9 w-9 flex-shrink-0 ${
            isRecording
              ? "bg-destructive text-destructive-foreground hover:bg-destructive/90 animate-pulse"
              : "text-muted-foreground hover:text-primary"
          }`}
          onClick={toggleRecording}
          disabled={isLoading}
          title={isRecording ? "Stop recording" : "Speak your message"}
        >
          {isRecording ? <MicOff className="h-4 w-4" /> : <Mic className="h-4 w-4" />}
        </Button>
      )}

      <textarea
        ref={inputRef}
        value={value}
        onChange={(e) => setValue(e.target.value)}
        onKeyDown={handleKeyDown}
        placeholder={
          isRecording
            ? "Listening..."
            : attachments.length > 0
              ? "Ask about the attached file..."
              : "Type a message..."
        }
        rows={1}
        className="flex-1 resize-none rounded-xl border bg-muted/50 px-3 py-2 text-sm focus:outline-none focus:ring-1 focus:ring-primary min-h-[36px] max-h-[100px]"
      />

      <Button
        size="icon"
        className="h-9 w-9 rounded-xl flex-shrink-0"
        onClick={handleSubmit}
        disabled={(!value.trim() && attachments.length === 0) || isLoading}
      >
        <Send className="h-4 w-4" />
      </Button>
      </div>
    </div>
  );
}
