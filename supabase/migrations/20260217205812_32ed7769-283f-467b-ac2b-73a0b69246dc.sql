
-- Table to store invoice correction patterns for AI learning
CREATE TABLE public.invoice_corrections (
  id UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
  user_id UUID NOT NULL,
  field_name TEXT NOT NULL, -- e.g. 'total_amount', 'vendor_name', 'invoice_number'
  original_value TEXT, -- what the AI extracted
  corrected_value TEXT NOT NULL, -- what the user corrected it to
  context TEXT, -- optional context like vendor name or receipt type
  created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now()
);

ALTER TABLE public.invoice_corrections ENABLE ROW LEVEL SECURITY;

CREATE POLICY "Users can insert their own corrections"
ON public.invoice_corrections FOR INSERT
WITH CHECK (auth.uid() = user_id);

CREATE POLICY "Service role can read all corrections"
ON public.invoice_corrections FOR SELECT
USING (auth.uid() IS NOT NULL);

-- Index for quick lookup
CREATE INDEX idx_invoice_corrections_field ON public.invoice_corrections (field_name, created_at DESC);
