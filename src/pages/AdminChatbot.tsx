import { Header } from "@/components/Header";
import { ChatbotSettings } from "@/components/admin/ChatbotSettings";
import { ChatbotLogs } from "@/components/admin/ChatbotLogs";
import { ChatbotSessions } from "@/components/admin/ChatbotSessions";
import { KnowledgeBase } from "@/components/admin/KnowledgeBase";
import { BulkReceiptTraining } from "@/components/admin/BulkReceiptTraining";
import { OutlookAgent } from "@/components/admin/OutlookAgent";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Settings, ScrollText, MessageSquare, Brain, Zap, Mail } from "lucide-react";
import { ChatbotWidget } from "@/components/chatbot/ChatbotWidget";

const AdminChatbot = () => {
  return (
    <div className="min-h-screen bg-background">
      <Header />
      <main className="container py-6 md:py-10 space-y-6">
        <div>
          <h1 className="text-2xl font-bold">Chatbot control panel</h1>
          <p className="text-muted-foreground">Manage settings, conversations, knowledge base and logs for TripEX AI</p>
        </div>

        <Tabs defaultValue="settings" className="space-y-6">
          <TabsList className="grid w-full max-w-3xl grid-cols-6">
            <TabsTrigger value="settings" className="gap-1.5">
              <Settings className="h-4 w-4" />
              Settings
            </TabsTrigger>
            <TabsTrigger value="knowledge" className="gap-1.5">
              <Brain className="h-4 w-4" />
              Knowledge base
            </TabsTrigger>
            <TabsTrigger value="bulk" className="gap-1.5">
              <Zap className="h-4 w-4" />
              Bulk training
            </TabsTrigger>
            <TabsTrigger value="outlook" className="gap-1.5">
              <Mail className="h-4 w-4" />
              Outlook
            </TabsTrigger>
            <TabsTrigger value="sessions" className="gap-1.5">
              <MessageSquare className="h-4 w-4" />
              Conversations
            </TabsTrigger>
            <TabsTrigger value="logs" className="gap-1.5">
              <ScrollText className="h-4 w-4" />
              Logs
            </TabsTrigger>
          </TabsList>

          <TabsContent value="settings">
            <ChatbotSettings />
          </TabsContent>
          <TabsContent value="knowledge">
            <KnowledgeBase />
          </TabsContent>
          <TabsContent value="bulk">
            <BulkReceiptTraining />
          </TabsContent>
          <TabsContent value="outlook">
            <OutlookAgent />
          </TabsContent>
          <TabsContent value="sessions">
            <ChatbotSessions />
          </TabsContent>
          <TabsContent value="logs">
            <ChatbotLogs />
          </TabsContent>
        </Tabs>

      </main>
      <ChatbotWidget />
    </div>
  );
};

export default AdminChatbot;
