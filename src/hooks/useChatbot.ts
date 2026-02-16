import { useState, useEffect, useCallback } from "react";
import { supabase } from "@/integrations/supabase/client";
import { useAuth } from "@/hooks/useAuth";
import { toast } from "sonner";

export interface ChatMessage {
  id: string;
  role: "user" | "assistant" | "system";
  content: string;
  intent?: string;
  metadata?: {
    actions?: string[];
    redirectPage?: string;
    data?: Record<string, any>;
    [key: string]: any;
  };
  created_at: string;
}

interface ChatbotConfig {
  bot_name: string;
  avatar_url: string | null;
  welcome_message: string;
  is_active: boolean;
}

export function useChatbot() {
  const { user } = useAuth();
  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [sessionId, setSessionId] = useState<string | null>(null);
  const [isLoading, setIsLoading] = useState(false);
  const [config, setConfig] = useState<ChatbotConfig | null>(null);

  // Load config
  const loadConfig = async () => {
    const { data } = await supabase
      .from("chatbot_config")
      .select("bot_name, avatar_url, welcome_message, is_active")
      .eq("is_active", true)
      .limit(1)
      .single();
    if (data) setConfig(data as ChatbotConfig);
  };

  useEffect(() => {
    loadConfig();
  }, []);

  // Load existing session messages
  useEffect(() => {
    if (!sessionId) return;
    const loadMessages = async () => {
      const { data } = await supabase
        .from("chat_messages")
        .select("*")
        .eq("session_id", sessionId)
        .order("created_at", { ascending: true });
      if (data) setMessages(data as ChatMessage[]);
    };
    loadMessages();
  }, [sessionId]);

  // NO realtime subscription - we handle messages via optimistic updates + API response only
  // This prevents duplicate messages caused by realtime INSERT events

  const sendMessage = useCallback(
    async (text: string) => {
      if (!user || !text.trim()) return;
      setIsLoading(true);

      // Optimistic user message
      const tempId = crypto.randomUUID();
      const tempMsg: ChatMessage = {
        id: tempId,
        role: "user",
        content: text,
        created_at: new Date().toISOString(),
      };
      setMessages((prev) => [...prev, tempMsg]);

      try {
        const now = new Date();
        const userLocalDate = now.toLocaleDateString("he-IL", { weekday: "long", year: "numeric", month: "long", day: "numeric" });
        const userLocalTime = now.toLocaleTimeString("he-IL", { hour: "2-digit", minute: "2-digit" });
        const timezone = Intl.DateTimeFormat().resolvedOptions().timeZone;

        const { data, error } = await supabase.functions.invoke("ai-router", {
          body: { text, type: "text", source: "web", sessionToken: sessionId, userDate: userLocalDate, userTime: userLocalTime, userTimezone: timezone },
        });

        if (error) throw error;

        if (data.session_id && !sessionId) {
          setSessionId(data.session_id);
        }

        // Add assistant message from API response
        const assistantMsg: ChatMessage = {
          id: crypto.randomUUID(),
          role: "assistant",
          content: data.text,
          metadata: {
            actions: data.actions || [],
            redirectPage: data.redirectPage || "",
            data: data.data || {},
          },
          created_at: new Date().toISOString(),
        };
        setMessages((prev) => [...prev, assistantMsg]);

        return { actions: data.actions, redirectPage: data.redirectPage, data: data.data };
      } catch (err: any) {
        console.error("Chat error:", err);
        toast.error("שגיאה בשליחת ההודעה");
        setMessages((prev) => prev.filter((m) => m.id !== tempId));
      } finally {
        setIsLoading(false);
      }
    },
    [user, sessionId]
  );

  const sendImage = useCallback(
    async (base64: string) => {
      if (!user) return;
      setIsLoading(true);

      const tempMsg: ChatMessage = {
        id: crypto.randomUUID(),
        role: "user",
        content: "📷 סריקת חשבונית...",
        created_at: new Date().toISOString(),
      };
      setMessages((prev) => [...prev, tempMsg]);

      try {
        const { data, error } = await supabase.functions.invoke("ai-router", {
          body: { text: base64, type: "image", source: "web", sessionToken: sessionId },
        });

        if (error) throw error;

        if (data.session_id && !sessionId) {
          setSessionId(data.session_id);
        }

        const assistantMsg: ChatMessage = {
          id: crypto.randomUUID(),
          role: "assistant",
          content: data.text || "החשבונית נסרקה בהצלחה!",
          metadata: {
            actions: data.actions || [],
            redirectPage: data.redirectPage || "",
            data: data.data || {},
          },
          created_at: new Date().toISOString(),
        };
        setMessages((prev) => [...prev, assistantMsg]);

        return { actions: data.actions, data: data.data };
      } catch (err: any) {
        console.error("Image scan error:", err);
        toast.error("שגיאה בסריקת החשבונית");
        setMessages((prev) => prev.filter((m) => m.id !== tempMsg.id));
      } finally {
        setIsLoading(false);
      }
    },
    [user, sessionId]
  );

  const startNewSession = useCallback(() => {
    setSessionId(null);
    setMessages([]);
  }, []);

  return {
    messages,
    isLoading,
    config,
    sessionId,
    sendMessage,
    sendImage,
    startNewSession,
  };
}
