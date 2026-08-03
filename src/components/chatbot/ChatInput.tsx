import { useState, useRef, useEffect } from "react";
import { Send, Camera, Paperclip, Mic, MicOff } from "lucide-react";
import { Button } from "@/components/ui/button";
import { pdfAllPagesToBase64 } from "@/lib/pdf-utils";
import { useToast } from "@/hooks/use-toast";

interface ChatInputProps {
  onSend: (message: string) => void;
  onImageCapture: (base64: string) => void;
  isLoading: boolean;
}

export function ChatInput({ onSend, onImageCapture, isLoading }: ChatInputProps) {
  const [value, setValue] = useState("");
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
    if (!value.trim() || isLoading) return;
    onSend(value.trim());
    setValue("");
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

  const handleFileChange = async (e: React.ChangeEvent<HTMLInputElement>) => {
    const filesList = e.target.files;
    if (!filesList || filesList.length === 0) return;
    const files = Array.from(filesList).slice(0, 5);
    if (filesList.length > 5) {
      toast({
        title: "Maximum 5 files",
        description: "Only the first 5 files will be scanned.",
      });
    }
    e.target.value = "";
    for (const file of files) {
      try {
        if (file.type === "application/pdf") {
          const pages = await pdfAllPagesToBase64(file);
          if (pages.length > 0) onImageCapture(pages[0]);
        } else {
          const base64 = await compressImage(file);
          onImageCapture(base64);
        }
      } catch (err) {
        console.error("File processing error:", err);
      }
    }
  };

  return (
    <div className="flex items-end gap-1.5 p-3 border-t bg-background">
      <Button
        type="button"
        variant="ghost"
        size="icon"
        className="h-9 w-9 flex-shrink-0 text-muted-foreground hover:text-primary"
        onClick={() => cameraRef.current?.click()}
        disabled={isLoading}
        title="Capture invoice"
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
        onChange={handleFileChange}
      />

      <Button
        type="button"
        variant="ghost"
        size="icon"
        className="h-9 w-9 flex-shrink-0 text-muted-foreground hover:text-primary"
        onClick={() => fileRef.current?.click()}
        disabled={isLoading}
        title="Choose up to 5 invoices"
      >
        <Paperclip className="h-4 w-4" />
      </Button>
      <input
        ref={fileRef}
        type="file"
        accept="image/*,application/pdf"
        multiple
        className="hidden"
        onChange={handleFileChange}
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
        placeholder={isRecording ? "Listening..." : "Type a message..."}
        rows={1}
        className="flex-1 resize-none rounded-xl border bg-muted/50 px-3 py-2 text-sm focus:outline-none focus:ring-1 focus:ring-primary min-h-[36px] max-h-[100px]"
        disabled={isLoading}
      />
      <Button
        size="icon"
        className="h-9 w-9 rounded-xl flex-shrink-0"
        onClick={handleSubmit}
        disabled={!value.trim() || isLoading}
      >
        <Send className="h-4 w-4" />
      </Button>
    </div>
  );
}
