
-- Improve search function to use ILIKE fallback for non-Latin languages
CREATE OR REPLACE FUNCTION public.search_knowledge(query_text TEXT, max_results INTEGER DEFAULT 5)
RETURNS TABLE(chunk_id UUID, document_id UUID, content TEXT, file_name TEXT, rank REAL)
LANGUAGE sql STABLE
SET search_path = public
AS $$
  -- Use both full-text search and ILIKE for better multilingual support
  SELECT DISTINCT ON (kc.id)
    kc.id as chunk_id,
    kc.document_id,
    kc.content,
    kd.file_name,
    COALESCE(ts_rank(kc.tsv, plainto_tsquery('simple', query_text)), 0) + 
    CASE WHEN kc.content ILIKE '%' || query_text || '%' THEN 1.0 ELSE 0.0 END as rank
  FROM public.knowledge_chunks kc
  JOIN public.knowledge_documents kd ON kd.id = kc.document_id
  WHERE kd.status = 'ready'
    AND (
      kc.tsv @@ plainto_tsquery('simple', query_text)
      OR kc.content ILIKE '%' || query_text || '%'
    )
  ORDER BY kc.id, rank DESC
  LIMIT max_results;
$$;
