
-- Knowledge base documents table
CREATE TABLE public.knowledge_documents (
  id UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
  file_name TEXT NOT NULL,
  file_type TEXT NOT NULL,
  file_url TEXT NOT NULL,
  file_size INTEGER,
  status TEXT NOT NULL DEFAULT 'pending', -- pending, processing, ready, error
  created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now(),
  updated_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now(),
  uploaded_by UUID REFERENCES auth.users(id)
);

-- Knowledge chunks for RAG
CREATE TABLE public.knowledge_chunks (
  id UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
  document_id UUID NOT NULL REFERENCES public.knowledge_documents(id) ON DELETE CASCADE,
  content TEXT NOT NULL,
  chunk_index INTEGER NOT NULL DEFAULT 0,
  created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now()
);

-- Full text search index on chunks
ALTER TABLE public.knowledge_chunks ADD COLUMN tsv tsvector 
  GENERATED ALWAYS AS (to_tsvector('simple', content)) STORED;
CREATE INDEX idx_knowledge_chunks_tsv ON public.knowledge_chunks USING GIN(tsv);

-- Enable RLS
ALTER TABLE public.knowledge_documents ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.knowledge_chunks ENABLE ROW LEVEL SECURITY;

-- Only admins can manage knowledge docs
CREATE POLICY "Admins can manage knowledge documents"
ON public.knowledge_documents FOR ALL
USING (public.has_role(auth.uid(), 'admin'));

CREATE POLICY "Admins can manage knowledge chunks"
ON public.knowledge_chunks FOR ALL
USING (EXISTS (
  SELECT 1 FROM public.knowledge_documents d 
  WHERE d.id = knowledge_chunks.document_id
));

-- Service role / edge functions can read chunks for RAG
CREATE POLICY "Authenticated users can read chunks"
ON public.knowledge_chunks FOR SELECT
USING (auth.uid() IS NOT NULL);

CREATE POLICY "Authenticated users can read documents"
ON public.knowledge_documents FOR SELECT
USING (auth.uid() IS NOT NULL);

-- Triggers
CREATE TRIGGER update_knowledge_documents_updated_at
BEFORE UPDATE ON public.knowledge_documents
FOR EACH ROW
EXECUTE FUNCTION public.update_updated_at_column();

-- Storage bucket for knowledge files
INSERT INTO storage.buckets (id, name, public) VALUES ('knowledge', 'knowledge', false);

-- Storage policies
CREATE POLICY "Admins can upload knowledge files"
ON storage.objects FOR INSERT
WITH CHECK (bucket_id = 'knowledge' AND public.has_role(auth.uid(), 'admin'));

CREATE POLICY "Admins can delete knowledge files"
ON storage.objects FOR DELETE
USING (bucket_id = 'knowledge' AND public.has_role(auth.uid(), 'admin'));

-- Anyone authenticated can read (for RAG)
CREATE POLICY "Authenticated can read knowledge files"
ON storage.objects FOR SELECT
USING (bucket_id = 'knowledge' AND auth.uid() IS NOT NULL);

-- Search function for RAG
CREATE OR REPLACE FUNCTION public.search_knowledge(query_text TEXT, max_results INTEGER DEFAULT 5)
RETURNS TABLE(chunk_id UUID, document_id UUID, content TEXT, file_name TEXT, rank REAL)
LANGUAGE sql STABLE
SET search_path = public
AS $$
  SELECT 
    kc.id as chunk_id,
    kc.document_id,
    kc.content,
    kd.file_name,
    ts_rank(kc.tsv, plainto_tsquery('simple', query_text)) as rank
  FROM public.knowledge_chunks kc
  JOIN public.knowledge_documents kd ON kd.id = kc.document_id
  WHERE kd.status = 'ready'
    AND kc.tsv @@ plainto_tsquery('simple', query_text)
  ORDER BY rank DESC
  LIMIT max_results;
$$;
