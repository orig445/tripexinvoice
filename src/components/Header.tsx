import { Receipt, LogOut, User, Shield, Bot, Brain, Lock, RotateCcw, BarChart3, Video } from "lucide-react";
import { useNavigate } from "react-router-dom";
import { useAuth } from "@/hooks/useAuth";
import { Button } from "@/components/ui/button";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { Avatar, AvatarFallback } from "@/components/ui/avatar";
import { Badge } from "@/components/ui/badge";

interface HeaderProps {
  onNewChat?: () => void;
}

export function Header({ onNewChat }: HeaderProps = {}) {
  const { user, role, isAdmin, signOut } = useAuth();
  const navigate = useNavigate();

  const getInitials = () => {
    if (!user?.email) return "U";
    return user.email.substring(0, 2).toUpperCase();
  };

  return (
    <header className="sticky top-0 z-40 w-full border-b bg-background/95 backdrop-blur supports-[backdrop-filter]:bg-background/60">
      <div className="container flex h-16 items-center justify-between gap-4">
        <div className="flex items-center gap-3">
          <div className="w-10 h-10 rounded-xl bg-gradient-to-br from-primary to-triplex-teal-light flex items-center justify-center shadow-md">
            <Receipt className="h-5 w-5 text-primary-foreground" />
          </div>
          <div>
            <h1 className="text-xl font-bold tracking-tight">
              <span className="text-gradient-primary">Tripex</span>
            </h1>
            <p className="text-xs text-muted-foreground -mt-0.5">Smart Invoice Management</p>
          </div>
        </div>

        <div className="flex items-center gap-2">
          {onNewChat && (
            <Button
              variant="outline"
              size="sm"
              className="gap-2"
              onClick={onNewChat}
            >
              <RotateCcw className="h-4 w-4" />
              <span className="hidden sm:inline">New chat</span>
            </Button>
          )}


          {user && (
            <Button
              variant="outline"
              size="sm"
              className="gap-2"
              onClick={() => navigate("/knowledge")}
            >
              <Brain className="h-4 w-4" />
              <span className="hidden sm:inline">Customer knowledge</span>
            </Button>
          )}

          {user && (
            <Button
              variant="outline"
              size="sm"
              className="gap-2"
              onClick={() => navigate("/knowledge-internal")}
            >
              <Lock className="h-4 w-4" />
              <span className="hidden sm:inline">Internal knowledge</span>
            </Button>
          )}

          {user && (
            <Button
              variant="outline"
              size="sm"
              className="gap-2"
              onClick={() => navigate("/chat-internal")}
            >
              <Lock className="h-4 w-4" />
              <span className="hidden sm:inline">Internal chat</span>
            </Button>
          )}

          {user && (
            <Button
              variant="outline"
              size="sm"
              className="gap-2"
              onClick={() => navigate("/qa")}
            >
              <BarChart3 className="h-4 w-4" />
              <span className="hidden sm:inline">Answer analytics</span>
            </Button>
          )}

          {isAdmin && (
            <Button
              variant="outline"
              size="sm"
              className="gap-2"
              onClick={() =>
                window.open(
                  "https://supabase-recordings-viewer.vercel.app/",
                  "_blank",
                  "noopener,noreferrer"
                )
              }
            >
              <Video className="h-4 w-4" />
              <span className="hidden sm:inline">Recordings</span>
            </Button>
          )}



          <Button
            variant="outline"
            size="sm"
            className="gap-2"
            onClick={() => navigate("/chat")}
          >
            <Bot className="h-4 w-4" />
            <span className="hidden sm:inline">Chat with Milo</span>
          </Button>


        {user && (
          <DropdownMenu>
            <DropdownMenuTrigger asChild>
              <Button variant="ghost" className="gap-2 px-2">
                <Avatar className="h-8 w-8">
                  <AvatarFallback className="bg-primary/10 text-primary text-sm">
                    {getInitials()}
                  </AvatarFallback>
                </Avatar>
                {isAdmin && (
                  <Badge variant="secondary" className="gap-1 hidden sm:flex">
                    <Shield className="h-3 w-3" />
                    Admin
                  </Badge>
                )}
              </Button>
            </DropdownMenuTrigger>
            <DropdownMenuContent align="end" className="w-56">
              <div className="px-2 py-1.5">
                <p className="text-sm font-medium">{user.email}</p>
                <p className="text-xs text-muted-foreground">
                  {role === "admin" ? "Administrator" : "User"}
                </p>
              </div>
              <DropdownMenuSeparator />
              <DropdownMenuItem className="gap-2">
                <User className="h-4 w-4" />
                Profile
              </DropdownMenuItem>
              <DropdownMenuItem className="gap-2" onClick={() => navigate("/chat")}>
                <Bot className="h-4 w-4" />
                Full Chat
              </DropdownMenuItem>
              {isAdmin && (
                <DropdownMenuItem className="gap-2" onClick={() => navigate("/admin/chatbot")}>
                  <Bot className="h-4 w-4" />
                  Chatbot Panel
                </DropdownMenuItem>
              )}
              <DropdownMenuSeparator />
              <DropdownMenuItem onClick={signOut} className="gap-2 text-destructive">
                <LogOut className="h-4 w-4" />
                Sign Out
              </DropdownMenuItem>
            </DropdownMenuContent>
          </DropdownMenu>
        )}
        </div>
      </div>
    </header>
  );
}
