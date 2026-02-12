import { Camera, Receipt, HelpCircle, Plane } from "lucide-react";
import { Button } from "@/components/ui/button";

interface QuickActionsProps {
  onAction: (text: string) => void;
}

export function QuickActions({ onAction }: QuickActionsProps) {
  const actions = [
    { icon: Camera, label: "סרוק קבלה", text: "סרוק לי קבלה" },
    { icon: Receipt, label: "הוסף הוצאה", text: "תוסיף הוצאה" },
    { icon: Plane, label: "בקשת נסיעה", text: "פתח לי בקשת נסיעה" },
    { icon: HelpCircle, label: "עזרה", text: "אני צריך עזרה" },
  ];

  return (
    <div className="flex flex-wrap gap-1.5 px-3 pb-2">
      {actions.map((a) => (
        <Button
          key={a.label}
          variant="outline"
          size="sm"
          className="h-7 text-xs gap-1 rounded-full"
          onClick={() => onAction(a.text)}
        >
          <a.icon className="h-3 w-3" />
          {a.label}
        </Button>
      ))}
    </div>
  );
}
