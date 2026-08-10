CREATE TABLE public.lesson_people (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  name text NOT NULL,
  note text,
  created_by uuid,
  created_at timestamptz NOT NULL DEFAULT now()
);

GRANT SELECT, INSERT ON public.lesson_people TO authenticated;
GRANT ALL ON public.lesson_people TO service_role;

ALTER TABLE public.lesson_people ENABLE ROW LEVEL SECURITY;

CREATE POLICY "Authenticated can read people" ON public.lesson_people
  FOR SELECT TO authenticated USING (true);
CREATE POLICY "Authenticated can add people" ON public.lesson_people
  FOR INSERT TO authenticated WITH CHECK (created_by = auth.uid());
CREATE POLICY "Admins manage people" ON public.lesson_people
  FOR ALL TO authenticated USING (has_role(auth.uid(), 'admin'::app_role)) WITH CHECK (has_role(auth.uid(), 'admin'::app_role));

ALTER TABLE public.bot_lessons
  ADD COLUMN IF NOT EXISTS scope text NOT NULL DEFAULT 'generic',
  ADD COLUMN IF NOT EXISTS person_id uuid REFERENCES public.lesson_people(id) ON DELETE SET NULL,
  ADD COLUMN IF NOT EXISTS user_types text[] NOT NULL DEFAULT ARRAY['user']::text[];