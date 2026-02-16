import { Header } from "@/components/Header";
import { ChatbotSettings } from "@/components/admin/ChatbotSettings";
import { ChatbotLogs } from "@/components/admin/ChatbotLogs";
import { ChatbotSessions } from "@/components/admin/ChatbotSessions";
import { KnowledgeBase } from "@/components/admin/KnowledgeBase";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Settings, ScrollText, MessageSquare, Brain } from "lucide-react";
import { ChatbotWidget } from "@/components/chatbot/ChatbotWidget";

const AdminChatbot = () => {
  return (
    <div className="min-h-screen bg-background" dir="rtl">
      <Header />
      <main className="container py-6 md:py-10 space-y-6">
        <div>
          <h1 className="text-2xl font-bold">פאנל שליטה - צ'אטבוט</h1>
          <p className="text-muted-foreground">ניהול הגדרות, שיחות, בסיס ידע ולוגים של TripEX AI</p>
        </div>

        <Tabs defaultValue="settings" className="space-y-6">
          <TabsList className="grid w-full max-w-lg grid-cols-4">
            <TabsTrigger value="settings" className="gap-1.5">
              <Settings className="h-4 w-4" />
              הגדרות
            </TabsTrigger>
            <TabsTrigger value="knowledge" className="gap-1.5">
              <Brain className="h-4 w-4" />
              בסיס ידע
            </TabsTrigger>
            <TabsTrigger value="sessions" className="gap-1.5">
              <MessageSquare className="h-4 w-4" />
              שיחות
            </TabsTrigger>
            <TabsTrigger value="logs" className="gap-1.5">
              <ScrollText className="h-4 w-4" />
              לוגים
            </TabsTrigger>
          </TabsList>

          <TabsContent value="settings">
            <ChatbotSettings />
          </TabsContent>
          <TabsContent value="knowledge">
            <KnowledgeBase />
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
