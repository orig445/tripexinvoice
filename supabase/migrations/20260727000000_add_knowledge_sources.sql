-- Source tracking for knowledge_documents so external connectors (SharePoint,
-- Zoho CRM) can sync documents idempotently: re-running a sync updates changed
-- items and skips unchanged ones instead of creating duplicates.

ALTER TABLE public.knowledge_documents
  ADD COLUMN IF NOT EXISTS source            text NOT NULL DEFAULT 'upload', -- 'upload' | 'sharepoint' | 'zoho_crm'
  ADD COLUMN IF NOT EXISTS external_id       text,        -- id of the item in the source system
  ADD COLUMN IF NOT EXISTS external_url      text,        -- link back to the item
  ADD COLUMN IF NOT EXISTS external_modified timestamptz; -- source "last modified" for change detection

-- One row per (source, external_id) so a sync can upsert by it. Manual uploads
-- keep external_id NULL and are excluded from the uniqueness constraint.
CREATE UNIQUE INDEX IF NOT EXISTS ux_knowledge_documents_source_external
  ON public.knowledge_documents (source, external_id)
  WHERE external_id IS NOT NULL;
