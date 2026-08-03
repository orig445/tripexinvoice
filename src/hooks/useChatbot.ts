import { useState, useEffect, useCallback, useRef } from "react";
import { supabase } from "@/integrations/supabase/client";
import { useAuth } from "@/hooks/useAuth";
import { toast } from "sonner";
import { sendChatMessage, sendImageForScan, type KnowledgeAudience } from "@/lib/api-service";

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

export function useChatbot(options?: { audience?: KnowledgeAudience; source?: string }) {
  const audience: KnowledgeAudience = options?.audience || "external";
  const source = options?.source || "web";
  const { user } = useAuth();
  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [sessionId, setSessionId] = useState<string | null>(null);
  const [isLoading, setIsLoading] = useState(false);
  const [config, setConfig] = useState<ChatbotConfig | null>(null);

  // Mirror of sessionId that updates SYNCHRONOUSLY. When several receipts are
  // sent in one batch (before React re-renders), each call must reuse the
  // session created by the first one instead of each opening its own session.
  const sessionIdRef = useRef<string | null>(null);
  useEffect(() => {
    sessionIdRef.current = sessionId;
  }, [sessionId]);

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

  // Restore the user's most recent conversation (per user + source) so the
  // history survives reloads and navigation between pages.
  useEffect(() => {
    if (!user || sessionIdRef.current) return;
    let cancelled = false;
    const restoreSession = async () => {
      const { data } = await supabase
        .from("chat_sessions")
        .select("id")
        .eq("user_id", user.id)
        .eq("source", source)
        .eq("status", "active")
        .order("updated_at", { ascending: false })
        .limit(1)
        .maybeSingle();
      if (cancelled || !data?.id || sessionIdRef.current) return;
      sessionIdRef.current = data.id;
      setSessionId(data.id);
    };
    restoreSession();
    return () => {
      cancelled = true;
    };
  }, [user?.id, source]);

  useEffect(() => {
    if (!sessionId) return;
    const loadMessages = async () => {
      const { data } = await supabase
        .from("chat_messages")
        .select("*")
        .eq("session_id", sessionId)
        .order("created_at", { ascending: true });
      // Only hydrate from the DB when we don't already have messages on screen.
      // Otherwise a freshly-created session would wipe the optimistic messages
      // of receipts still being processed in the same batch.
      if (data && data.length > 0) {
        setMessages((prev) => (prev.length > 0 ? prev : (data as ChatMessage[])));
      }
    };
    loadMessages();
  }, [sessionId]);


  const sendMessage = useCallback(
    async (text: string) => {
      if (!user || !text.trim()) return;
      setIsLoading(true);

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
        const userLocalDate = now.toLocaleDateString("en-US", { weekday: "long", year: "numeric", month: "long", day: "numeric" });
        const userLocalTime = now.toLocaleTimeString("en-US", { hour: "2-digit", minute: "2-digit" });
        const timezone = Intl.DateTimeFormat().resolvedOptions().timeZone;

        const { data, error } = await sendChatMessage({
          text, source, sessionToken: sessionIdRef.current, userDate: userLocalDate, userTime: userLocalTime, userTimezone: timezone, audience,
        });

        if (error) throw error;

        if (data.session_id && !sessionIdRef.current) {
          sessionIdRef.current = data.session_id;
          setSessionId(data.session_id);
        }

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
        toast.error("Error sending message");
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
        content: "📷 Scanning invoice...",
        created_at: new Date().toISOString(),
      };
      setMessages((prev) => [...prev, tempMsg]);

      try {
        const { data, error } = await sendImageForScan({
          base64, source, sessionToken: sessionIdRef.current, audience,
        });

        if (error) throw error;

        if (data.session_id && !sessionIdRef.current) {
          sessionIdRef.current = data.session_id;
          setSessionId(data.session_id);
        }

        const assistantMsg: ChatMessage = {
          id: crypto.randomUUID(),
          role: "assistant",
          content: data.text || "Invoice scanned successfully!",
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
        toast.error("Error scanning invoice");
        setMessages((prev) => prev.filter((m) => m.id !== tempMsg.id));
      } finally {
        setIsLoading(false);
      }
    },
    [user, sessionId]
  );

  const startNewSession = useCallback(() => {
    const previous = sessionIdRef.current;
    sessionIdRef.current = null;
    setSessionId(null);
    setMessages([]);
    // Archive the previous conversation so it is kept in history but not
    // restored automatically next time.
    if (previous) {
      supabase
        .from("chat_sessions")
        .update({ status: "closed" })
        .eq("id", previous)
        .then(({ error }) => {
          if (error) console.error("Failed to close session:", error);
        });
    }
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
