
-- Chatbot config table (singleton for admin settings)
CREATE TABLE public.chatbot_config (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  bot_name text NOT NULL DEFAULT 'TripEX AI',
  avatar_url text,
  welcome_message text NOT NULL DEFAULT 'שלום! אני TripEX AI, העוזר האישי שלך לניהול נסיעות והוצאות. איך אוכל לעזור?',
  system_prompt text NOT NULL DEFAULT 'You are TripEX AI, a Personal Assistant for Travel & Expense Management.
Detect user intent and respond accordingly:

- Help/guidance -> respond with JSON: {"intent": "help", "action": "none", "text": "<your helpful response>"}
- Scan receipt -> respond with JSON: {"intent": "scan", "action": "camera", "text": "<your response>"}
- Analyze data (BI) -> respond with JSON: {"intent": "bi", "action": "none", "text": "<your response>"}
- Online booking -> respond with JSON: {"intent": "online", "action": "redirect", "text": "<your response>"}
- Manage expenses -> respond with JSON: {"intent": "expense", "action": "redirect", "text": "<your response>"}
- General chat -> respond with JSON: {"intent": "general", "action": "none", "text": "<your natural response>"}

Always respond in the user''s language. Always return valid JSON with: intent, action, text.',
  model_name text NOT NULL DEFAULT 'meta.llama-4-maverick-17b-128e-instruct-fp8',
  max_tokens integer NOT NULL DEFAULT 1024,
  temperature numeric NOT NULL DEFAULT 0.3,
  is_active boolean NOT NULL DEFAULT true,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now()
);

ALTER TABLE public.chatbot_config ENABLE ROW LEVEL SECURITY;

CREATE POLICY "Admins can manage chatbot config"
ON public.chatbot_config FOR ALL
USING (has_role(auth.uid(), 'admin'::app_role));

CREATE POLICY "Authenticated users can read active config"
ON public.chatbot_config FOR SELECT
TO authenticated
USING (is_active = true);

-- Insert default config
INSERT INTO public.chatbot_config (bot_name) VALUES ('TripEX AI');

-- Chat sessions
CREATE TABLE public.chat_sessions (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id uuid NOT NULL,
  source text NOT NULL DEFAULT 'web',
  status text NOT NULL DEFAULT 'active',
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now()
);

ALTER TABLE public.chat_sessions ENABLE ROW LEVEL SECURITY;

CREATE POLICY "Users can manage their own sessions"
ON public.chat_sessions FOR ALL
USING (auth.uid() = user_id);

CREATE POLICY "Admins can view all sessions"
ON public.chat_sessions FOR SELECT
USING (has_role(auth.uid(), 'admin'::app_role));

-- Chat messages
CREATE TABLE public.chat_messages (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  session_id uuid NOT NULL REFERENCES public.chat_sessions(id) ON DELETE CASCADE,
  role text NOT NULL DEFAULT 'user',
  content text NOT NULL,
  intent text,
  metadata jsonb DEFAULT '{}'::jsonb,
  created_at timestamptz NOT NULL DEFAULT now()
);

ALTER TABLE public.chat_messages ENABLE ROW LEVEL SECURITY;

CREATE POLICY "Users can manage messages in their sessions"
ON public.chat_messages FOR ALL
USING (
  EXISTS (
    SELECT 1 FROM public.chat_sessions
    WHERE id = chat_messages.session_id AND user_id = auth.uid()
  )
);

CREATE POLICY "Admins can view all messages"
ON public.chat_messages FOR SELECT
USING (has_role(auth.uid(), 'admin'::app_role));

-- Chatbot logs
CREATE TABLE public.chatbot_logs (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  session_id uuid REFERENCES public.chat_sessions(id) ON DELETE SET NULL,
  user_id uuid,
  event_type text NOT NULL,
  details jsonb DEFAULT '{}'::jsonb,
  created_at timestamptz NOT NULL DEFAULT now()
);

ALTER TABLE public.chatbot_logs ENABLE ROW LEVEL SECURITY;

CREATE POLICY "Admins can view all logs"
ON public.chatbot_logs FOR ALL
USING (has_role(auth.uid(), 'admin'::app_role));

-- Enable realtime on chat_messages
ALTER PUBLICATION supabase_realtime ADD TABLE public.chat_messages;

-- Add update triggers
CREATE TRIGGER update_chatbot_config_updated_at
BEFORE UPDATE ON public.chatbot_config
FOR EACH ROW EXECUTE FUNCTION public.update_updated_at_column();

CREATE TRIGGER update_chat_sessions_updated_at
BEFORE UPDATE ON public.chat_sessions
FOR EACH ROW EXECUTE FUNCTION public.update_updated_at_column();
