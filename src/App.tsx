import { Toaster } from "@/components/ui/toaster";
import { Toaster as Sonner } from "@/components/ui/sonner";
import { TooltipProvider } from "@/components/ui/tooltip";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { BrowserRouter, Routes, Route } from "react-router-dom";
import { AuthProvider } from "@/hooks/useAuth";
import { ProtectedRoute } from "@/components/ProtectedRoute";
import Index from "./pages/Index";
import Auth from "./pages/Auth";
import AdminChatbot from "./pages/AdminChatbot";
import Chat from "./pages/Chat";
import KnowledgeUpload from "./pages/KnowledgeUpload";
import KnowledgeUploadInternal from "./pages/KnowledgeUploadInternal";
import ChatInternal from "./pages/ChatInternal";
import NotFound from "./pages/NotFound";

const queryClient = new QueryClient();

const App = () => (
  <QueryClientProvider client={queryClient}>
    <TooltipProvider>
      <Toaster />
      <Sonner />
      <BrowserRouter>
        <AuthProvider>
          <Routes>
            <Route path="/auth" element={<Auth />} />
            <Route
              path="/"
              element={
                <ProtectedRoute>
                  <Index />
                </ProtectedRoute>
              }
            />
            <Route path="/chat" element={<Chat />} />
            <Route
              path="/knowledge"
              element={
                <ProtectedRoute>
                  <KnowledgeUpload />
                </ProtectedRoute>
              }
            />
            <Route
              path="/knowledge-internal"
              element={
                <ProtectedRoute>
                  <KnowledgeUploadInternal />
                </ProtectedRoute>
              }
            />
            <Route
              path="/chat-internal"
              element={
                <ProtectedRoute>
                  <ChatInternal />
                </ProtectedRoute>
              }
            />
            <Route
              path="/admin/chatbot"
              element={
                <ProtectedRoute>
                  <AdminChatbot />
                </ProtectedRoute>
              }
            />
            {/* ADD ALL CUSTOM ROUTES ABOVE THE CATCH-ALL "*" ROUTE */}
            <Route path="*" element={<NotFound />} />
          </Routes>
        </AuthProvider>
      </BrowserRouter>
    </TooltipProvider>
  </QueryClientProvider>
);

export default App;
