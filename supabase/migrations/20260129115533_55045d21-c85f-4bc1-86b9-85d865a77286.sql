-- Create invoices table to store analyzed invoice data
CREATE TABLE public.invoices (
  id UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
  created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now(),
  updated_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now(),
  
  -- Invoice details
  invoice_number TEXT,
  invoice_date DATE,
  due_date DATE,
  
  -- Vendor/Supplier info
  vendor_name TEXT,
  vendor_address TEXT,
  vendor_phone TEXT,
  vendor_email TEXT,
  vendor_id TEXT, -- Business ID/Tax number
  
  -- Customer info
  customer_name TEXT,
  customer_address TEXT,
  
  -- Financial details
  subtotal DECIMAL(12,2),
  tax_amount DECIMAL(12,2),
  total_amount DECIMAL(12,2),
  currency TEXT DEFAULT 'ILS',
  
  -- Items (stored as JSON array)
  line_items JSONB DEFAULT '[]'::jsonb,
  
  -- Additional extracted data
  notes TEXT,
  payment_terms TEXT,
  
  -- Original image reference
  image_url TEXT,
  
  -- Raw AI response for debugging
  raw_ai_response TEXT,
  
  -- Status
  status TEXT DEFAULT 'processed' CHECK (status IN ('processing', 'processed', 'error'))
);

-- Enable RLS
ALTER TABLE public.invoices ENABLE ROW LEVEL SECURITY;

-- Public read/write for now (no auth required for demo)
CREATE POLICY "Anyone can view invoices" 
ON public.invoices 
FOR SELECT 
USING (true);

CREATE POLICY "Anyone can create invoices" 
ON public.invoices 
FOR INSERT 
WITH CHECK (true);

CREATE POLICY "Anyone can update invoices" 
ON public.invoices 
FOR UPDATE 
USING (true);

CREATE POLICY "Anyone can delete invoices" 
ON public.invoices 
FOR DELETE 
USING (true);

-- Create function to update timestamps
CREATE OR REPLACE FUNCTION public.update_updated_at_column()
RETURNS TRIGGER AS $$
BEGIN
  NEW.updated_at = now();
  RETURN NEW;
END;
$$ LANGUAGE plpgsql SET search_path = public;

-- Create trigger for automatic timestamp updates
CREATE TRIGGER update_invoices_updated_at
BEFORE UPDATE ON public.invoices
FOR EACH ROW
EXECUTE FUNCTION public.update_updated_at_column();

-- Create storage bucket for invoice images
INSERT INTO storage.buckets (id, name, public) VALUES ('invoices', 'invoices', true);

-- Storage policies
CREATE POLICY "Anyone can view invoice images" 
ON storage.objects 
FOR SELECT 
USING (bucket_id = 'invoices');

CREATE POLICY "Anyone can upload invoice images" 
ON storage.objects 
FOR INSERT 
WITH CHECK (bucket_id = 'invoices');

CREATE POLICY "Anyone can update invoice images" 
ON storage.objects 
FOR UPDATE 
USING (bucket_id = 'invoices');

CREATE POLICY "Anyone can delete invoice images" 
ON storage.objects 
FOR DELETE 
USING (bucket_id = 'invoices');