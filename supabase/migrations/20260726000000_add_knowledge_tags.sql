-- Add tagging columns to knowledge_documents so each document can be scoped to a
-- business domain + content type, with an optional free-text hint that guides the
-- agent on when to use it. Mirrors the .NET backend (KnowledgeDocument entity).

ALTER TABLE public.knowledge_documents
  ADD COLUMN IF NOT EXISTS domain      text,
  ADD COLUMN IF NOT EXISTS doc_type    text,
  ADD COLUMN IF NOT EXISTS description text;
