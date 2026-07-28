
CREATE TABLE public.outlook_agent_config (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  enabled BOOLEAN NOT NULL DEFAULT false,
  mode TEXT NOT NULL DEFAULT 'draft' CHECK (mode IN ('draft','auto_reply')),
  folder TEXT NOT NULL DEFAULT 'inbox',
  signature TEXT NOT NULL DEFAULT 'Best regards,\nMilo — TripEX Support',
  last_run_at TIMESTAMPTZ,
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE public.outlook_processed_emails (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  message_id TEXT NOT NULL UNIQUE,
  conversation_id TEXT,
  from_address TEXT,
  from_name TEXT,
  subject TEXT,
  received_at TIMESTAMPTZ,
  body_preview TEXT,
  reply_text TEXT,
  status TEXT NOT NULL DEFAULT 'pending' CHECK (status IN ('pending','draft_created','sent','failed','skipped')),
  error_message TEXT,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX idx_outlook_processed_created ON public.outlook_processed_emails (created_at DESC);

GRANT SELECT, INSERT, UPDATE, DELETE ON public.outlook_agent_config TO authenticated;
GRANT ALL ON public.outlook_agent_config TO service_role;
GRANT SELECT, INSERT, UPDATE, DELETE ON public.outlook_processed_emails TO authenticated;
GRANT ALL ON public.outlook_processed_emails TO service_role;

ALTER TABLE public.outlook_agent_config ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.outlook_processed_emails ENABLE ROW LEVEL SECURITY;

CREATE POLICY "Admins manage outlook config"
  ON public.outlook_agent_config FOR ALL
  TO authenticated
  USING (public.has_role(auth.uid(), 'admin'))
  WITH CHECK (public.has_role(auth.uid(), 'admin'));

CREATE POLICY "Admins view outlook emails"
  ON public.outlook_processed_emails FOR SELECT
  TO authenticated
  USING (public.has_role(auth.uid(), 'admin'));

CREATE POLICY "Admins manage outlook emails"
  ON public.outlook_processed_emails FOR ALL
  TO authenticated
  USING (public.has_role(auth.uid(), 'admin'))
  WITH CHECK (public.has_role(auth.uid(), 'admin'));

INSERT INTO public.outlook_agent_config (enabled, mode) VALUES (false, 'draft');
