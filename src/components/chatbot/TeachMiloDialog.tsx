import { useEffect, useState } from "react";
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogDescription, DialogFooter } from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import { Textarea } from "@/components/ui/textarea";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Checkbox } from "@/components/ui/checkbox";
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
import { Plus } from "lucide-react";

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

interface LessonPerson {
  id: string;
  name: string;
}

const USER_TYPES = [
  { value: "user", label: "Regular user" },
  { value: "coordinator", label: "Coordinator" },
  { value: "finance", label: "Finance" },
  { value: "admin", label: "Admin" },
];

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
  const [scope, setScope] = useState<"generic" | "user_specific">("generic");
  const [userTypes, setUserTypes] = useState<string[]>(["user"]);
  const [people, setPeople] = useState<LessonPerson[]>([]);
  const [personId, setPersonId] = useState<string>("");
  const [newPerson, setNewPerson] = useState("");
  const [addingPerson, setAddingPerson] = useState(false);
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    setQuestion(defaultQuestion);
  }, [defaultQuestion]);

  useEffect(() => {
    if (!open) return;
    supabase
      .from("lesson_people")
      .select("id, name")
      .order("name")
      .then(({ data }) => setPeople(data || []));
  }, [open]);

  const toggleUserType = (value: string, checked: boolean) => {
    setUserTypes((prev) => (checked ? [...new Set([...prev, value])] : prev.filter((v) => v !== value)));
  };

  const handleAddPerson = async () => {
    if (!user) return;
    const name = newPerson.trim();
    if (!name) return;
    setAddingPerson(true);
    const { data, error } = await supabase
      .from("lesson_people")
      .insert({ name, created_by: user.id })
      .select("id, name")
      .single();
    setAddingPerson(false);
    if (error || !data) {
      console.error("Add person error:", error);
      toast.error("Could not save the user");
      return;
    }
    setPeople((prev) => [...prev, data].sort((a, b) => a.name.localeCompare(b.name)));
    setPersonId(data.id);
    setNewPerson("");
    toast.success(`${data.name} added`);
  };

  const handleSave = async () => {
    if (!user) {
      toast.error("Please sign in to teach Milo");
      return;
    }
    if (!question.trim() || !answer.trim()) {
      toast.error("Please fill in both the question and the correct answer");
      return;
    }
    if (userTypes.length === 0) {
      toast.error("Pick at least one user type");
      return;
    }
    if (scope === "user_specific" && !personId) {
      toast.error("Pick the user this lesson belongs to");
      return;
    }
    setSaving(true);
    const { error } = await supabase.from("bot_lessons").insert({
      question: question.trim(),
      answer: answer.trim(),
      audience,
      source,
      scope,
      person_id: scope === "user_specific" ? personId : null,
      user_type: userTypes[0],
      user_types: userTypes,
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
      <DialogContent className="sm:max-w-lg max-h-[90vh] overflow-y-auto">
        <DialogHeader>
          <DialogTitle>Teach Milo 🦊</DialogTitle>
          <DialogDescription>
            Correct Milo's answer. Your lesson is shared with the whole team and Milo will use it in
            future conversations.
          </DialogDescription>
        </DialogHeader>

        <div className="space-y-3">
          <div className="space-y-1.5">
            <Label htmlFor="lesson-scope">Answer type</Label>
            <Select value={scope} onValueChange={(v) => setScope(v as "generic" | "user_specific")}>
              <SelectTrigger id="lesson-scope">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="generic">Generic answer (everyone)</SelectItem>
                <SelectItem value="user_specific">Specific to a user</SelectItem>
              </SelectContent>
            </Select>
          </div>

          {scope === "user_specific" && (
            <div className="space-y-1.5 rounded-md border p-3">
              <Label htmlFor="lesson-person">User</Label>
              <Select value={personId} onValueChange={setPersonId}>
                <SelectTrigger id="lesson-person">
                  <SelectValue placeholder="Select a saved user" />
                </SelectTrigger>
                <SelectContent>
                  {people.length === 0 && (
                    <div className="px-2 py-1.5 text-sm text-muted-foreground">No users saved yet</div>
                  )}
                  {people.map((p) => (
                    <SelectItem key={p.id} value={p.id}>
                      {p.name}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
              <div className="flex gap-2 pt-1">
                <Input
                  value={newPerson}
                  onChange={(e) => setNewPerson(e.target.value)}
                  placeholder="Add a new user (name)"
                  onKeyDown={(e) => {
                    if (e.key === "Enter") {
                      e.preventDefault();
                      handleAddPerson();
                    }
                  }}
                />
                <Button
                  type="button"
                  variant="outline"
                  onClick={handleAddPerson}
                  disabled={addingPerson || !newPerson.trim()}
                  className="gap-1 shrink-0"
                >
                  <Plus className="h-4 w-4" />
                  Add
                </Button>
              </div>
            </div>
          )}

          <div className="space-y-1.5">
            <Label>This lesson applies to (you can pick more than one)</Label>
            <div className="grid grid-cols-2 gap-2">
              {USER_TYPES.map((t) => (
                <label
                  key={t.value}
                  className="flex items-center gap-2 rounded-md border px-3 py-2 text-sm cursor-pointer"
                >
                  <Checkbox
                    checked={userTypes.includes(t.value)}
                    onCheckedChange={(c) => toggleUserType(t.value, c === true)}
                  />
                  {t.label}
                </label>
              ))}
            </div>
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
