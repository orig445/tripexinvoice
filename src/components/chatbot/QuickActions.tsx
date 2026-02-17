import { Camera, Receipt, HelpCircle, Plane } from "lucide-react";
import { Button } from "@/components/ui/button";

interface QuickActionsProps {
  onAction: (text: string) => void;
}

export function QuickActions({ onAction }: QuickActionsProps) {
  const actions = [
    { icon: Camera, label: "Scan Receipt", text: "Scan a receipt for me" },
    { icon: Receipt, label: "Add Expense", text: "Add an expense" },
    { icon: Plane, label: "Travel Request", text: "Open a travel request" },
    { icon: HelpCircle, label: "Help", text: "I need help" },
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
