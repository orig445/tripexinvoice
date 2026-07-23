// ─────────────────────────────────────────────────────────────────────────────
// Built-in domain knowledge for TripEX AI (Milo).
//
// This is the assistant's own "brain" about the ComBTAS / TAS travel & expense
// system. It is ALWAYS injected into the system prompt so Milo can answer the
// user's specific case directly — without telling the user to go read an
// external user guide, chapter, or page number.
//
// TAS_REPORTS_KNOWLEDGE is sourced from the official "Analyst Report TAS"
// catalog (report codes, names and explanations). When a user asks which
// report shows a certain kind of data, Milo should name the exact report
// (code + name) and explain what it contains — in plain language.
// ─────────────────────────────────────────────────────────────────────────────

export const TAS_SYSTEM_KNOWLEDGE = `## Built-in System Knowledge (TAS / ComBTAS Travel & Expense)

Use this knowledge to answer the user's SPECIFIC situation directly. Explain what
is happening and exactly what they should do inside the TripEX/TAS system.

### Travel (Trip) statuses — the full life-cycle, in order
A trip moves through these statuses from start to finish:
1. **Draft** — Once an employee opens a travel request, it is in Draft. Still editable; excluded from most reports.
2. **TR Approval** — Principle approval for the Travel Request.
3. **Coordinator Approval** — The Travel Coordinator needs to accept or reject the request.
4. **Reservation** — From the moment the Travel Coordinator accepts until proposals arrive and are selected from the Travel Agent.
5. **Proposal Approval** — Round of approvals: the chosen proposals need to be approved.
6. **Approved** — The entire trip is approved.
7. **Issued** — After the Coordinator clicks "Travel Issue" on the FINAL screen. The whole trip is correct, approved and ready to go (tickets/bookings issued).
8. **Active** — The period of time the employee is abroad.
9. **Travel Completed** — The trip is finished.
10. **Expense Report** — After the employee declares End-Trip Confirmation, they can send their expenses for approval.
11. **Expense Approval** — Round of approvals for the travel expenses.
12. **Expense Approved** — All expenses were approved and a payment was made.
13. **Closed** — The Coordinator marks that all travel issues were finalized and approved.

Cancellation path (can happen from various stages):
- **Pending for Cancellation** — When someone cancels a trip, the cancellation request is sent for approval.
- **Cancelled** — The trip was cancelled.

Note: there are SEVERAL distinct approval stages — **TR Approval**, **Coordinator Approval**,
**Proposal Approval** (all early, about approving the trip itself) and **Expense Approval**
(late, about approving the money spent). If a user just says "approval", figure out which one
from context.

### Why "I can't submit an expense report while my trip is in an 'Approval' status"
The **Expense Report** stage (step 10) comes near the END of the life-cycle — only AFTER
Approved → Issued → Active → Travel Completed. If the trip is still sitting in an early
approval stage (TR Approval, Coordinator Approval or Proposal Approval), the trip itself
isn't approved and issued yet, so there is nothing to close with an expense report — that
is why the option isn't available yet. What to do:
- Identify which approval the trip is waiting on and follow up with that approver (the manager
  for TR/Proposal Approval, or the Travel Coordinator for Coordinator Approval) so the trip can
  move forward to Approved → Issued.
- After you actually travel and the trip reaches **Travel Completed**, open the **End-Trip
  Confirmation** page, then send the expense report for approval — it will move to Expense
  Approval and then Expense Approved once signed and paid.

### Statuses that appear in reports
Most reports exclude **Draft** and **Cancelled** trips. Report data is based on Approved /
Issued / Closed items and on **Expense Approved** expense reports.`;

export const TAS_REPORTS_KNOWLEDGE = `## Built-in Reports Catalog (TAS Analyst Reports)

When a user asks about reports — which report to use, where to see certain data,
or what a report shows — answer using this catalog. Give the exact **report code
and name** and a short plain-language explanation. Do NOT tell the user to look it
up in an external guide.

### Finance › Accrual
- **7002 — Accrual**: Manages the company's accrual (provisions) report; built per company regulations.

### Finance › Budget
- **8001 — Budget Matrix**: Budget structure per cost center — planned travels plus actual travels.
- **8002 — Budget by Division**: How much of the budget each division/department/cost center has already used.
- **8003 — Budget by Company**: Budget already used at company level, including approved travels.
- **8004 — Budget by Division 2**: Budget usage broken down by different areas/regions.
- **8005 — Budget By Travel**: Budget exploitation sorted by project, budget section, employee, trip type — includes approved and not-yet-signed travels.

### Finance › General
- **6001 — Travel Days for Insurance**: Total travel days (status Approved→Closed) per travel, with traveler ID, birthday, company, units, destination and health declaration — everything an insurance company asks for.
- **10002 — Special Days Report**: Trips where the traveler declared vacation days during the trip (declared on the "End Trip Confirmation" page), for trips whose expense report was approved.

### Finance › Invoice
- **1014 — Saving**: The highest, lowest and chosen agent/self-booking proposals and the difference (savings) between them.
- **7001 — Invoice List**: All statement/invoice details — status (approved/open/closed), supplier, invoice no., coordinator, service, travel details. Filter by departure/issue/due/invoice date. Filtered by the traveler's company.
- **7010 — Invoice List Interface Data**: Invoice list including the interface-transfer data — invoice status vs. the interface (Open/Approved/Closed), file-send date, and batch number.
- **7011 — Invoice List by Service**: Invoices split by flights, hotels, cars and other — deeper analysis via many parameters. Filtered by the trip's project company.
- **7012 — Issued Services With/Without Invoice**: All issued services (flights/hotels/cars) by trip no., showing which agent invoices arrived or are missing. Filter by supplier, invoice state and trip status.

### Finance › Redundancy (per-diem / tax redundancy)
- **7003 — Redundancy by Travel**: Approved statements & expenses vs. the system's redundancy tables (Israeli tax law), showing amount in $, permitted amount and redundancy amount — filtered by travel.
- **7004 — Redundancy by Month**: Same redundancy calculation, filtered by month.
- **7005 — Redundancy By Year**: Same redundancy calculation, filtered by year.

### Management › Expense
- **7006 — Expense Report by Type**: Only "Expense Approved" expenses, derived from statements + the traveler's expense report — with TAS no., agency, invoice no., dates, destination, units, project, employee, total.

### Travel Reports › Air (flights)
- **1005 — Volume by Agent**: Volume per agent by service (flight/hotel/car/other) for issued travels; cost per project/budget item. Table + graph.
- **2001 — Airline Volume**: Flight volume by airline for issued (ticketed) flights only; excludes cancelled.
- **2002 — Volume By Destination**: Flight volume by destination (continent, city) for issued travels; excludes cancelled.
- **2003 — Agreement Targets**: Airline agreements vs. actual issued flights (agreements are entered by the customer).
- **2004 — Airline Class of Booking**: Reservations by airline, ticket no., flight class, ticket price plus AP taxes.
- **2007 — Airline Class of Booking by Date Sent**: Class-of-booking view by the date the booking was sent.
- **2005 — Volume By Airline and Destination**: How many flights per destination by each airline (issued status), with company, unit, dates, employee, project.
- **2006 — UATP / AirPlus Tickets**: All flights paid with a UATP/AirPlus card, with details and total cost.
- **2008 — Flight Change Tracking**: Tracks flight changes.
- **2009 — Volume By Agency and Airline**: Volume broken down by agency and airline.
- **2010 — Airline Tickets**: Number of tickets per airline; shows the highest flight class on the ticket (BUSINESS if any leg is business; ECONOMY if no class match is found).

### Travel Reports › Hotel
- **3001 — Hotels By Agency and Destination**: Hotel details for issued travels by destination and agent.
- **3002 — Hotels By Destination (detailed)**: Accommodation costs by destination/country/city — employee, cost center, hotel, nights, average cost per day, % of total accommodation cost.

### Travel Reports › Car
- **4001 — Cars By Rental Company and Destination (detailed)**: Issued car rentals by rental company; excludes cancelled/draft.
- **4002 — Cars By Agency and Destination**: Chosen & issued car quotes by destination city (a quote with no destination city, or in cancelled/draft, is excluded).

### Travel Reports › Expense
- **1002 — Travel Expenses**: All approved expenses filtered by worker/unit/project/expense type — with travel no., unit, name, dates, expense type, form of payment, original amount, amount in USD, project.
- **1004 — Top Spender**: The biggest spenders in the organization (approved expenses + issued), filterable by traveler name or expense type.
- **1015 — Travel Management Control**: All non-draft, non-cancelled trips from the travel request; shows ACCEPTANCE DATE and ISSUE DATE and the gap between them; filter by coordinator. Does not include out-of-trip expense reports.
- **7012 — Travel Expenses Tracking**: Trip expenses by expense type for the trips in the system.

### Travel Reports › General
- **1001 — Travel by Categories**: Volume of the travel budget by expense category (flight, car, hotel, hospitality, internet, …) from employee expenses and issued quotes; can compare periods.
- **1003 — Advance Purchase Summary**: By how many days each trip was pre-ordered (reservation-to-departure); excludes cancelled/draft.
- **1006 — Travel Status**: All trips opened in the system (excluding draft) with their status — filter by agent, unit, dates, destination, project. A key day-to-day report for coordinators and finance.
- **1008 — Travel Status with Amounts**: Travel Status plus amounts.
- **1009 — Travel Status Angola**: Travel Status for flights to Angola only.
- **1011 — Travel Status Approver Manager**: Travel Status plus the approving manager (filter only).
- **1007 — No. of Trips by Day of Week**: Issued travels by departure day of week (excludes cancelled) — helps control travel patterns.
- **1012 — Refund**: Split into flights, hotels, cars; shows all refund requests to the agent and refunds approved/received, with vendor, employee, travel no., ticketing date, segment status and cancellation cause.
- **1013 — Trip Summarizing**: All trips except Draft; data from approved/closed invoices, plus employee expenses/per-diem that entered after the expense report was sent for approval.
- **1016 — Suits Report**: Number of days each employee is entitled to a suit allowance (custom development).
- **2011 — Top Destinations**: Number of flight tickets per destination (country/city) with reservation counts, total and average cost — shows the most popular destinations, with a graph.`;

export const TAS_SUPPORT_CASES = `## Built-in Troubleshooting Knowledge (from real support cases)

Common real-world issues coordinators and travel agents run into, and how they are
resolved. When a user describes one of these, answer with the concrete fix or
explanation below — don't send them elsewhere.

### A new agent doesn't appear in the "Pass Trip" / "Pass to agent" list
The Pass-Trip list only shows agents that are (1) linked to the same supplier as the
current user, (2) NOT in "Blocked" status, and (3) NOT marked "Left the company".
If a newly created agent is missing from the list, check that the agent is linked to
the relevant supplier/client and is active (not Blocked, not Left the company). Note
that the list may also show agents who aren't synced to a specific client — that is
expected current behavior.

### Train / railway booking asks for an airline, or a train comes in as a flight
Train and rail services must use the **Transportation** offer type, not the **Flight**
offer type. The Flight type requires an airline company, which is why a train row won't
let you close/Issue without one. If a train quote was imported (e.g. from the booster)
as a Flight offer, change its offer type to **Transportation** — then it will not ask for
an airline and you can Issue it normally.

### "Approve all matched invoices" (bulk approval) fails with an error
Bulk-approving matched invoices can hit an error while approving them one-by-one still
works. Workaround: approve the matched invoices individually for now. This is a known
issue that gets resolved in a system version update, so once you're on the latest
version the bulk "Approve all matched invoices" action works again.

### Can't cancel or change a Statement/invoice (button does nothing)
If clicking **Cancel** or **Change** on a Statement (invoice) does nothing, first log
out and log back in and try again. To upload a corrected invoice you must first cancel
the existing statement. If it still won't cancel after re-logging in, it's a known issue
that is fixed in a system version update.

### An agent can't see a travel request (TAS) that was emailed to them
By design, when several agents receive the same travel-request email, only ONE agent
sees that TAS under their "Tasks". This is intentional — it prevents two agents from
pulling the same offer from the GDS at the same time (which causes GDS errors). To work
with a TAS that isn't under your tasks: search for the **TAS number** in the search bar
and open it. To move it under your own Tasks, do a **Pass Trip** from inside the TAS.

### A travel request wasn't auto-sent to the agent ("mail TR to agent")
If a TAS wasn't automatically sent to the agent, a common cause is that when the TAS was
approved, the trip's approval round (rotation) was in **Blocked** status, so the
automatic "mail TR to agent" didn't fire. Changing the rotation AFTER the TAS was
approved does not apply retroactively. Fix: from the **Travel Request** screen, manually
send **"mail TR to agent"** so the TAS reaches the agent.

### Changing the default currency (e.g. to EUR)
The system's default currency can be changed, but it only affects **new** quotes —
existing quotes keep the currency they were created with (you can still change a quote's
currency manually). The **Total USD** column is a fixed system column and cannot be
removed.`;
