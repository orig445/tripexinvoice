import { useState } from "react";
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogDescription, DialogFooter } from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import { Textarea } from "@/components/ui/textarea";
import { Label } from "@/components/ui/label";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { supabase } from "@/integrations/supabase/client";
import { useAuth } from "@/hooks/useAuth";
import { toast } from "sonner";

interface TeachMiloDialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  /** The user question this lesson answers */
  defaultQuestion?: string;
  /** Milo's answer that was wrong (used as a starting point) */
  defaultAnswer?: string;
  audience?: "external" | "internal";
  source?: string;
}

export function TeachMiloDialog({
  open,
  onOpenChange,
  defaultQuestion = "",
  defaultAnswer = "",
  audience = "external",
  source = "web",
}: TeachMiloDialogProps) {
  const { user } = useAuth();
  const [question, setQuestion] = useState(defaultQuestion);
  const [answer, setAnswer] = useState("");
  const [userType, setUserType] = useState("user");
  const [saving, setSaving] = useState(false);

  const handleSave = async () => {
    if (!user) {
      toast.error("Please sign in to teach Milo");
      return;
    }
    if (!question.trim() || !answer.trim()) {
      toast.error("Please fill in both the question and the correct answer");
      return;
    }
    setSaving(true);
    const { error } = await supabase.from("bot_lessons").insert({
      question: question.trim(),
      answer: answer.trim(),
      audience,
      source,
      user_type: userType,
      taught_by: user.id,
    });
    setSaving(false);
    if (error) {
      console.error("Teach Milo error:", error);
      toast.error("Could not save the lesson");
      return;
    }
    toast.success("Thanks! Milo learned that 🦊");
    setAnswer("");
    onOpenChange(false);
  };

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="sm:max-w-lg">
        <DialogHeader>
          <DialogTitle>Teach Milo 🦊</DialogTitle>
          <DialogDescription>
            Correct Milo's answer. Your lesson is shared with the whole team and Milo will use it in
            future conversations.
          </DialogDescription>
        </DialogHeader>

        <div className="space-y-3">
          <div className="space-y-1.5">
            <Label htmlFor="lesson-user-type">This lesson applies to</Label>
            <Select value={userType} onValueChange={setUserType}>
              <SelectTrigger id="lesson-user-type">
                <SelectValue placeholder="Select user type" />
              </SelectTrigger>
              <SelectContent>
              <SelectItem value="user">Regular user</SelectItem>
              <SelectItem value="coordinator">Coordinator</SelectItem>
              <SelectItem value="finance">Finance / כספים</SelectItem>
              <SelectItem value="admin">Admin</SelectItem>
            </SelectContent>
            </Select>
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="lesson-question">When a user asks…</Label>
            <Textarea
              id="lesson-question"
              value={question}
              onChange={(e) => setQuestion(e.target.value)}
              rows={2}
              placeholder="e.g. What does GRAND TOTAL mean in a TAS?"
            />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="lesson-answer">…Milo should answer</Label>
            <Textarea
              id="lesson-answer"
              value={answer}
              onChange={(e) => setAnswer(e.target.value)}
              rows={5}
              placeholder="Write the correct answer here"
            />
          </div>
          {defaultAnswer && (
            <p className="text-xs text-muted-foreground line-clamp-3">
              Milo said: “{defaultAnswer}”
            </p>
          )}
        </div>

        <DialogFooter>
          <Button variant="ghost" onClick={() => onOpenChange(false)}>
            Cancel
          </Button>
          <Button onClick={handleSave} disabled={saving}>
            {saving ? "Saving…" : "Teach Milo"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
