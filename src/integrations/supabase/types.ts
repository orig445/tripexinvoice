export type Json =
  | string
  | number
  | boolean
  | null
  | { [key: string]: Json | undefined }
  | Json[]

export type Database = {
  // Allows to automatically instantiate createClient with right options
  // instead of createClient<Database, { PostgrestVersion: 'XX' }>(URL, KEY)
  __InternalSupabase: {
    PostgrestVersion: "14.1"
  }
  public: {
    Tables: {
      bot_lessons: {
        Row: {
          answer: string
          audience: string
          created_at: string
          id: string
          is_approved: boolean
          question: string
          source: string
          taught_by: string | null
          updated_at: string
          user_type: string
          votes: number
        }
        Insert: {
          answer: string
          audience?: string
          created_at?: string
          id?: string
          is_approved?: boolean
          question: string
          source?: string
          taught_by?: string | null
          updated_at?: string
          user_type?: string
          votes?: number
        }
        Update: {
          answer?: string
          audience?: string
          created_at?: string
          id?: string
          is_approved?: boolean
          question?: string
          source?: string
          taught_by?: string | null
          updated_at?: string
          user_type?: string
          votes?: number
        }
        Relationships: []
      }
      chat_messages: {
        Row: {
          content: string
          created_at: string
          id: string
          intent: string | null
          metadata: Json | null
          role: string
          session_id: string
        }
        Insert: {
          content: string
          created_at?: string
          id?: string
          intent?: string | null
          metadata?: Json | null
          role?: string
          session_id: string
        }
        Update: {
          content?: string
          created_at?: string
          id?: string
          intent?: string | null
          metadata?: Json | null
          role?: string
          session_id?: string
        }
        Relationships: [
          {
            foreignKeyName: "chat_messages_session_id_fkey"
            columns: ["session_id"]
            isOneToOne: false
            referencedRelation: "chat_sessions"
            referencedColumns: ["id"]
          },
        ]
      }
      chat_sessions: {
        Row: {
          created_at: string
          id: string
          source: string
          status: string
          updated_at: string
          user_id: string
        }
        Insert: {
          created_at?: string
          id?: string
          source?: string
          status?: string
          updated_at?: string
          user_id: string
        }
        Update: {
          created_at?: string
          id?: string
          source?: string
          status?: string
          updated_at?: string
          user_id?: string
        }
        Relationships: []
      }
      chatbot_config: {
        Row: {
          avatar_url: string | null
          bot_name: string
          created_at: string
          id: string
          is_active: boolean
          max_tokens: number
          model_name: string
          system_prompt: string
          temperature: number
          updated_at: string
          welcome_message: string
        }
        Insert: {
          avatar_url?: string | null
          bot_name?: string
          created_at?: string
          id?: string
          is_active?: boolean
          max_tokens?: number
          model_name?: string
          system_prompt?: string
          temperature?: number
          updated_at?: string
          welcome_message?: string
        }
        Update: {
          avatar_url?: string | null
          bot_name?: string
          created_at?: string
          id?: string
          is_active?: boolean
          max_tokens?: number
          model_name?: string
          system_prompt?: string
          temperature?: number
          updated_at?: string
          welcome_message?: string
        }
        Relationships: []
      }
      chatbot_logs: {
        Row: {
          created_at: string
          details: Json | null
          event_type: string
          id: string
          session_id: string | null
          user_id: string | null
        }
        Insert: {
          created_at?: string
          details?: Json | null
          event_type: string
          id?: string
          session_id?: string | null
          user_id?: string | null
        }
        Update: {
          created_at?: string
          details?: Json | null
          event_type?: string
          id?: string
          session_id?: string | null
          user_id?: string | null
        }
        Relationships: [
          {
            foreignKeyName: "chatbot_logs_session_id_fkey"
            columns: ["session_id"]
            isOneToOne: false
            referencedRelation: "chat_sessions"
            referencedColumns: ["id"]
          },
        ]
      }
      invoice_corrections: {
        Row: {
          context: string | null
          corrected_value: string
          created_at: string
          field_name: string
          id: string
          original_value: string | null
          user_id: string
        }
        Insert: {
          context?: string | null
          corrected_value: string
          created_at?: string
          field_name: string
          id?: string
          original_value?: string | null
          user_id: string
        }
        Update: {
          context?: string | null
          corrected_value?: string
          created_at?: string
          field_name?: string
          id?: string
          original_value?: string | null
          user_id?: string
        }
        Relationships: []
      }
      invoices: {
        Row: {
          created_at: string
          currency: string | null
          customer_address: string | null
          customer_name: string | null
          due_date: string | null
          id: string
          image_url: string | null
          invoice_date: string | null
          invoice_number: string | null
          line_items: Json | null
          notes: string | null
          payment_terms: string | null
          raw_ai_response: string | null
          status: string | null
          subtotal: number | null
          tax_amount: number | null
          total_amount: number | null
          updated_at: string
          user_id: string | null
          vendor_address: string | null
          vendor_email: string | null
          vendor_id: string | null
          vendor_name: string | null
          vendor_phone: string | null
        }
        Insert: {
          created_at?: string
          currency?: string | null
          customer_address?: string | null
          customer_name?: string | null
          due_date?: string | null
          id?: string
          image_url?: string | null
          invoice_date?: string | null
          invoice_number?: string | null
          line_items?: Json | null
          notes?: string | null
          payment_terms?: string | null
          raw_ai_response?: string | null
          status?: string | null
          subtotal?: number | null
          tax_amount?: number | null
          total_amount?: number | null
          updated_at?: string
          user_id?: string | null
          vendor_address?: string | null
          vendor_email?: string | null
          vendor_id?: string | null
          vendor_name?: string | null
          vendor_phone?: string | null
        }
        Update: {
          created_at?: string
          currency?: string | null
          customer_address?: string | null
          customer_name?: string | null
          due_date?: string | null
          id?: string
          image_url?: string | null
          invoice_date?: string | null
          invoice_number?: string | null
          line_items?: Json | null
          notes?: string | null
          payment_terms?: string | null
          raw_ai_response?: string | null
          status?: string | null
          subtotal?: number | null
          tax_amount?: number | null
          total_amount?: number | null
          updated_at?: string
          user_id?: string | null
          vendor_address?: string | null
          vendor_email?: string | null
          vendor_id?: string | null
          vendor_name?: string | null
          vendor_phone?: string | null
        }
        Relationships: []
      }
      knowledge_chunks: {
        Row: {
          chunk_index: number
          content: string
          created_at: string
          document_id: string
          id: string
          tsv: unknown
        }
        Insert: {
          chunk_index?: number
          content: string
          created_at?: string
          document_id: string
          id?: string
          tsv?: unknown
        }
        Update: {
          chunk_index?: number
          content?: string
          created_at?: string
          document_id?: string
          id?: string
          tsv?: unknown
        }
        Relationships: [
          {
            foreignKeyName: "knowledge_chunks_document_id_fkey"
            columns: ["document_id"]
            isOneToOne: false
            referencedRelation: "knowledge_documents"
            referencedColumns: ["id"]
          },
        ]
      }
      knowledge_documents: {
        Row: {
          audience: string
          created_at: string
          description: string | null
          doc_type: string | null
          domain: string | null
          file_name: string
          file_size: number | null
          file_type: string
          file_url: string
          id: string
          status: string
          updated_at: string
          uploaded_by: string | null
        }
        Insert: {
          audience?: string
          created_at?: string
          description?: string | null
          doc_type?: string | null
          domain?: string | null
          file_name: string
          file_size?: number | null
          file_type: string
          file_url: string
          id?: string
          status?: string
          updated_at?: string
          uploaded_by?: string | null
        }
        Update: {
          audience?: string
          created_at?: string
          description?: string | null
          doc_type?: string | null
          domain?: string | null
          file_name?: string
          file_size?: number | null
          file_type?: string
          file_url?: string
          id?: string
          status?: string
          updated_at?: string
          uploaded_by?: string | null
        }
        Relationships: []
      }
      ocr_training_patterns: {
        Row: {
          confidence: number
          country: string | null
          created_at: string
          field_name: string
          id: string
          pattern_rule: string
          source_count: number
          updated_at: string
        }
        Insert: {
          confidence?: number
          country?: string | null
          created_at?: string
          field_name: string
          id?: string
          pattern_rule: string
          source_count?: number
          updated_at?: string
        }
        Update: {
          confidence?: number
          country?: string | null
          created_at?: string
          field_name?: string
          id?: string
          pattern_rule?: string
          source_count?: number
          updated_at?: string
        }
        Relationships: []
      }
      ocr_training_samples: {
        Row: {
          corrections: Json | null
          country: string | null
          created_at: string
          extracted_data: Json
          id: string
          image_hash: string | null
          is_correct: boolean | null
          is_verified: boolean | null
          updated_at: string
          user_id: string | null
        }
        Insert: {
          corrections?: Json | null
          country?: string | null
          created_at?: string
          extracted_data?: Json
          id?: string
          image_hash?: string | null
          is_correct?: boolean | null
          is_verified?: boolean | null
          updated_at?: string
          user_id?: string | null
        }
        Update: {
          corrections?: Json | null
          country?: string | null
          created_at?: string
          extracted_data?: Json
          id?: string
          image_hash?: string | null
          is_correct?: boolean | null
          is_verified?: boolean | null
          updated_at?: string
          user_id?: string | null
        }
        Relationships: []
      }
      outlook_agent_config: {
        Row: {
          enabled: boolean
          folder: string
          id: string
          last_run_at: string | null
          mode: string
          signature: string
          updated_at: string
        }
        Insert: {
          enabled?: boolean
          folder?: string
          id?: string
          last_run_at?: string | null
          mode?: string
          signature?: string
          updated_at?: string
        }
        Update: {
          enabled?: boolean
          folder?: string
          id?: string
          last_run_at?: string | null
          mode?: string
          signature?: string
          updated_at?: string
        }
        Relationships: []
      }
      outlook_processed_emails: {
        Row: {
          body_preview: string | null
          conversation_id: string | null
          created_at: string
          error_message: string | null
          from_address: string | null
          from_name: string | null
          id: string
          message_id: string
          received_at: string | null
          reply_text: string | null
          status: string
          subject: string | null
        }
        Insert: {
          body_preview?: string | null
          conversation_id?: string | null
          created_at?: string
          error_message?: string | null
          from_address?: string | null
          from_name?: string | null
          id?: string
          message_id: string
          received_at?: string | null
          reply_text?: string | null
          status?: string
          subject?: string | null
        }
        Update: {
          body_preview?: string | null
          conversation_id?: string | null
          created_at?: string
          error_message?: string | null
          from_address?: string | null
          from_name?: string | null
          id?: string
          message_id?: string
          received_at?: string | null
          reply_text?: string | null
          status?: string
          subject?: string | null
        }
        Relationships: []
      }
      profiles: {
        Row: {
          created_at: string
          display_name: string | null
          email: string | null
          id: string
          updated_at: string
          user_id: string
        }
        Insert: {
          created_at?: string
          display_name?: string | null
          email?: string | null
          id?: string
          updated_at?: string
          user_id: string
        }
        Update: {
          created_at?: string
          display_name?: string | null
          email?: string | null
          id?: string
          updated_at?: string
          user_id?: string
        }
        Relationships: []
      }
      user_roles: {
        Row: {
          created_at: string
          id: string
          role: Database["public"]["Enums"]["app_role"]
          user_id: string
        }
        Insert: {
          created_at?: string
          id?: string
          role?: Database["public"]["Enums"]["app_role"]
          user_id: string
        }
        Update: {
          created_at?: string
          id?: string
          role?: Database["public"]["Enums"]["app_role"]
          user_id?: string
        }
        Relationships: []
      }
      whatsapp_messages: {
        Row: {
          chat_id: string
          content: string
          created_at: string
          id: string
          role: string
          sender_name: string | null
        }
        Insert: {
          chat_id: string
          content: string
          created_at?: string
          id?: string
          role: string
          sender_name?: string | null
        }
        Update: {
          chat_id?: string
          content?: string
          created_at?: string
          id?: string
          role?: string
          sender_name?: string | null
        }
        Relationships: []
      }
    }
    Views: {
      [_ in never]: never
    }
    Functions: {
      get_user_role: {
        Args: { _user_id: string }
        Returns: Database["public"]["Enums"]["app_role"]
      }
      has_role: {
        Args: {
          _role: Database["public"]["Enums"]["app_role"]
          _user_id: string
        }
        Returns: boolean
      }
      search_knowledge: {
        Args: { max_results?: number; query_text: string }
        Returns: {
          chunk_id: string
          content: string
          document_id: string
          file_name: string
          rank: number
        }[]
      }
    }
    Enums: {
      app_role: "admin" | "user"
    }
    CompositeTypes: {
      [_ in never]: never
    }
  }
}

type DatabaseWithoutInternals = Omit<Database, "__InternalSupabase">

type DefaultSchema = DatabaseWithoutInternals[Extract<keyof Database, "public">]

export type Tables<
  DefaultSchemaTableNameOrOptions extends
    | keyof (DefaultSchema["Tables"] & DefaultSchema["Views"])
    | { schema: keyof DatabaseWithoutInternals },
  TableName extends DefaultSchemaTableNameOrOptions extends {
    schema: keyof DatabaseWithoutInternals
  }
    ? keyof (DatabaseWithoutInternals[DefaultSchemaTableNameOrOptions["schema"]]["Tables"] &
        DatabaseWithoutInternals[DefaultSchemaTableNameOrOptions["schema"]]["Views"])
    : never = never,
> = DefaultSchemaTableNameOrOptions extends {
  schema: keyof DatabaseWithoutInternals
}
  ? (DatabaseWithoutInternals[DefaultSchemaTableNameOrOptions["schema"]]["Tables"] &
      DatabaseWithoutInternals[DefaultSchemaTableNameOrOptions["schema"]]["Views"])[TableName] extends {
      Row: infer R
    }
    ? R
    : never
  : DefaultSchemaTableNameOrOptions extends keyof (DefaultSchema["Tables"] &
        DefaultSchema["Views"])
    ? (DefaultSchema["Tables"] &
        DefaultSchema["Views"])[DefaultSchemaTableNameOrOptions] extends {
        Row: infer R
      }
      ? R
      : never
    : never

export type TablesInsert<
  DefaultSchemaTableNameOrOptions extends
    | keyof DefaultSchema["Tables"]
    | { schema: keyof DatabaseWithoutInternals },
  TableName extends DefaultSchemaTableNameOrOptions extends {
    schema: keyof DatabaseWithoutInternals
  }
    ? keyof DatabaseWithoutInternals[DefaultSchemaTableNameOrOptions["schema"]]["Tables"]
    : never = never,
> = DefaultSchemaTableNameOrOptions extends {
  schema: keyof DatabaseWithoutInternals
}
  ? DatabaseWithoutInternals[DefaultSchemaTableNameOrOptions["schema"]]["Tables"][TableName] extends {
      Insert: infer I
    }
    ? I
    : never
  : DefaultSchemaTableNameOrOptions extends keyof DefaultSchema["Tables"]
    ? DefaultSchema["Tables"][DefaultSchemaTableNameOrOptions] extends {
        Insert: infer I
      }
      ? I
      : never
    : never

export type TablesUpdate<
  DefaultSchemaTableNameOrOptions extends
    | keyof DefaultSchema["Tables"]
    | { schema: keyof DatabaseWithoutInternals },
  TableName extends DefaultSchemaTableNameOrOptions extends {
    schema: keyof DatabaseWithoutInternals
  }
    ? keyof DatabaseWithoutInternals[DefaultSchemaTableNameOrOptions["schema"]]["Tables"]
    : never = never,
> = DefaultSchemaTableNameOrOptions extends {
  schema: keyof DatabaseWithoutInternals
}
  ? DatabaseWithoutInternals[DefaultSchemaTableNameOrOptions["schema"]]["Tables"][TableName] extends {
      Update: infer U
    }
    ? U
    : never
  : DefaultSchemaTableNameOrOptions extends keyof DefaultSchema["Tables"]
    ? DefaultSchema["Tables"][DefaultSchemaTableNameOrOptions] extends {
        Update: infer U
      }
      ? U
      : never
    : never

export type Enums<
  DefaultSchemaEnumNameOrOptions extends
    | keyof DefaultSchema["Enums"]
    | { schema: keyof DatabaseWithoutInternals },
  EnumName extends DefaultSchemaEnumNameOrOptions extends {
    schema: keyof DatabaseWithoutInternals
  }
    ? keyof DatabaseWithoutInternals[DefaultSchemaEnumNameOrOptions["schema"]]["Enums"]
    : never = never,
> = DefaultSchemaEnumNameOrOptions extends {
  schema: keyof DatabaseWithoutInternals
}
  ? DatabaseWithoutInternals[DefaultSchemaEnumNameOrOptions["schema"]]["Enums"][EnumName]
  : DefaultSchemaEnumNameOrOptions extends keyof DefaultSchema["Enums"]
    ? DefaultSchema["Enums"][DefaultSchemaEnumNameOrOptions]
    : never

export type CompositeTypes<
  PublicCompositeTypeNameOrOptions extends
    | keyof DefaultSchema["CompositeTypes"]
    | { schema: keyof DatabaseWithoutInternals },
  CompositeTypeName extends PublicCompositeTypeNameOrOptions extends {
    schema: keyof DatabaseWithoutInternals
  }
    ? keyof DatabaseWithoutInternals[PublicCompositeTypeNameOrOptions["schema"]]["CompositeTypes"]
    : never = never,
> = PublicCompositeTypeNameOrOptions extends {
  schema: keyof DatabaseWithoutInternals
}
  ? DatabaseWithoutInternals[PublicCompositeTypeNameOrOptions["schema"]]["CompositeTypes"][CompositeTypeName]
  : PublicCompositeTypeNameOrOptions extends keyof DefaultSchema["CompositeTypes"]
    ? DefaultSchema["CompositeTypes"][PublicCompositeTypeNameOrOptions]
    : never

export const Constants = {
  public: {
    Enums: {
      app_role: ["admin", "user"],
    },
  },
} as const
