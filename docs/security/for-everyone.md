# How TaskBoard keeps your tasks safe

*A plain-language guide. No technical background needed.*

If you've ever stayed in a hotel, you already understand how TaskBoard protects your data.
Every time you click something in TaskBoard, your request passes four checkpoints before it
touches your tasks:

| Checkpoint | Hotel version | What it does |
| ---------- | ------------- | ------------ |
| 1. The doorman | Stops a crowd from rushing the door | Slows down anyone trying too many times too fast |
| 2. Check-in desk | Checks your ID, hands you a key card | Signing in with your email and password |
| 3. The door lock | Reads your key card every time | Checks your card on every single click |
| 4. Your room | Opens for you and nobody else | You only ever see your own tasks |

Prefer slides? The same story is in the **[security walkthrough slideshow](../presentations/security-walkthrough.html)**.

---

## Two words people mix up

**Authentication** asks *"Who are you?"* **Authorization** asks *"Are you allowed to do this?"*

> **Everyday example: the airport.** At security, an agent checks your **driver's license**:
> are you who you say you are? At the gate, the agent checks your **boarding pass**: are you
> allowed on *this* plane? Same trip, two different checks.

TaskBoard always does the first check before the second. It can't decide what you're allowed
to do until it knows who you are.

## Signing in

You type your email and password (or use Google). TaskBoard compares your password against a
scrambled copy on file. It never stores your real password, so even its own records can't
reveal it.

When you sign in you get two things:

- **A key card** that's shown at every door. It stops working after **15 minutes**.
- **A renewal pass**, kept locked away where the web page itself can't reach it. It's good
  for **7 days**.

The key card is used constantly, so it expires quickly. The renewal pass is used rarely and
stays locked up.

## Why the key card can't be faked

The key card lists who you are and when it expires, and anyone could read it. What makes it
safe is its **hologram**.

> **Everyday example: your driver's license.** Anyone can read your name and birthday off
> it, but the hologram makes a fake obvious. Scratch out the birth year and write a new one,
> and any bouncer can tell.

TaskBoard's key card has a digital hologram that only TaskBoard knows how to make. Change one
letter on the card, for example to pretend you're a manager, and the hologram no longer matches.

## What the door checks, every time

1. **Is the hologram real?** Made by TaskBoard, and nothing on the card has been changed.
2. **Right hotel?** A card from somewhere else doesn't work here.
3. **Still in date?** Not past its 15-minute expiry.
4. **Still a guest?** A closed account is turned away.
5. **Does it fit the current lock?** If the locks were changed, old cards fail (see below).

If any check fails, TaskBoard simply asks you to sign in again.

## Staying signed in

Your key card expires every 15 minutes, but you never notice. Behind the scenes, TaskBoard
hands in your renewal pass and gets back a fresh key card **and a brand-new renewal pass**.
Each renewal pass works only once.

> **Everyday example: a single-use day pass.** Picture a gym that gives you a new pass each
> morning. You hand in yesterday's and get today's. A lost pass is only good for a day.

## You only see your own tasks

- **You need a key card** for every task and category. Only signing in, signing up and
  renewing are open to the public.
- **Every lookup is filtered by the name on your card.** You can't see, change or delete
  anyone else's tasks.
- **Managers** can lock down a compromised account, but even a manager can't read your tasks.

> **Everyday example: asking for a room number.** Call a hotel and ask which room a guest is
> in, and they won't tell you. They won't even confirm the person is staying there. Ask
> TaskBoard for someone else's task and it just says "not found."

## Lost your keys? Change the locks.

> **Everyday example: your front door.** If you lose your house keys, you don't chase down
> every copy. You change the lock. Now **every key cut for the old lock stops working**:
> yours, the spare under the mat, and the one a stranger picked up.

TaskBoard does exactly this:

- **Change the lock.** Every key card carries your account's current lock code. Change the
  code and every card ever issued to you stops working instantly, on every device.
- **Take back the spares.** All renewal passes are cancelled, so nobody can get a new card.
  If a pass that was already handed in ever shows up again, someone copied it, and TaskBoard
  locks everything down.

The **"Sign out everywhere"** button does both at once. Closing an account has the same effect.

## Too many wrong guesses? You wait.

> **Everyday example: your phone's passcode.** Enter the wrong code a few times and your phone
> says "try again in 1 minute." You barely notice. A thief guessing thousands of codes gets
> nowhere.

- **10 sign-in attempts a minute** from one home or office connection.
- **200 clicks a minute** for everything else, far more than a person ever needs.
- Go over the limit and TaskBoard tells you how many seconds to wait, then lets you back in.

The doorman stands *before* the check-in desk, so a flood of attempts is stopped before
TaskBoard spends any effort checking passwords. One limitation: people sharing one connection,
such as an office, share one allowance.

---

## Quick glossary

| You'll hear | It means | Technical name |
| ----------- | -------- | -------------- |
| Key card | Proof you signed in, checked on every click | Access token (JWT) |
| Hologram | Tamper seal only TaskBoard can make | Signature (HMAC-SHA256) |
| Renewal pass | Gets you a new key card without your password | Refresh token |
| Lock code | Changing it cancels every key card at once | Security stamp |
| Doorman | Limits how fast anyone can knock | Rate limiter |
| "Please sign in" | The door said no | HTTP 401 |
| "Slow down" | Too many tries | HTTP 429 |

Want the technical version? See **[TaskBoard security for developers](for-developers.md)**.
