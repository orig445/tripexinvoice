
-- OCR Training Samples table
CREATE TABLE public.ocr_training_samples (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id UUID REFERENCES auth.users(id) ON DELETE SET NULL,
  image_hash TEXT,
  country TEXT DEFAULT 'IL',
  extracted_data JSONB NOT NULL DEFAULT '{}'::jsonb,
  is_verified BOOLEAN DEFAULT false,
  is_correct BOOLEAN,
  corrections JSONB,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- OCR Training Patterns table
CREATE TABLE public.ocr_training_patterns (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  field_name TEXT NOT NULL,
  pattern_rule TEXT NOT NULL,
  country TEXT,
  confidence NUMERIC NOT NULL DEFAULT 0,
  source_count INTEGER NOT NULL DEFAULT 0,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Enable RLS
ALTER TABLE public.ocr_training_samples ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.ocr_training_patterns ENABLE ROW LEVEL SECURITY;

-- RLS: Admins can manage all samples
CREATE POLICY "Admins can manage training samples" ON public.ocr_training_samples
  FOR ALL TO public USING (public.has_role(auth.uid(), 'admin'));

-- RLS: Authenticated users can insert their own samples
CREATE POLICY "Users can insert training samples" ON public.ocr_training_samples
  FOR INSERT TO authenticated WITH CHECK (auth.uid() = user_id);

-- RLS: Users can read their own samples
CREATE POLICY "Users can read own training samples" ON public.ocr_training_samples
  FOR SELECT TO authenticated USING (auth.uid() = user_id);

-- RLS: Admins can manage patterns
CREATE POLICY "Admins can manage training patterns" ON public.ocr_training_patterns
  FOR ALL TO public USING (public.has_role(auth.uid(), 'admin'));

-- RLS: Authenticated users can read patterns
CREATE POLICY "Authenticated can read patterns" ON public.ocr_training_patterns
  FOR SELECT TO authenticated USING (auth.uid() IS NOT NULL);

-- Updated_at triggers
CREATE TRIGGER update_ocr_training_samples_updated_at
  BEFORE UPDATE ON public.ocr_training_samples
  FOR EACH ROW EXECUTE FUNCTION public.update_updated_at_column();

CREATE TRIGGER update_ocr_training_patterns_updated_at
  BEFORE UPDATE ON public.ocr_training_patterns
  FOR EACH ROW EXECUTE FUNCTION public.update_updated_at_column();
