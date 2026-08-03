CREATE TABLE public.bot_lessons (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  question text NOT NULL,
  answer text NOT NULL,
  audience text NOT NULL DEFAULT 'external',
  source text NOT NULL DEFAULT 'web',
  taught_by uuid REFERENCES auth.users(id) ON DELETE SET NULL,
  is_approved boolean NOT NULL DEFAULT true,
  votes integer NOT NULL DEFAULT 1,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now()
);

GRANT SELECT, INSERT, UPDATE, DELETE ON public.bot_lessons TO authenticated;
GRANT ALL ON public.bot_lessons TO service_role;

ALTER TABLE public.bot_lessons ENABLE ROW LEVEL SECURITY;

CREATE POLICY "Authenticated can read approved lessons"
  ON public.bot_lessons FOR SELECT TO authenticated
  USING (is_approved = true OR taught_by = auth.uid() OR has_role(auth.uid(), 'admin'::app_role));

CREATE POLICY "Authenticated can teach lessons"
  ON public.bot_lessons FOR INSERT TO authenticated
  WITH CHECK (taught_by = auth.uid());

CREATE POLICY "Admins manage lessons"
  ON public.bot_lessons FOR ALL TO authenticated
  USING (has_role(auth.uid(), 'admin'::app_role))
  WITH CHECK (has_role(auth.uid(), 'admin'::app_role));

CREATE TRIGGER update_bot_lessons_updated_at
  BEFORE UPDATE ON public.bot_lessons
  FOR EACH ROW EXECUTE FUNCTION public.update_updated_at_column();

CREATE INDEX idx_bot_lessons_audience ON public.bot_lessons (audience, is_approved, created_at DESC);