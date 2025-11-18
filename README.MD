TcpQueue

This application communicates with a target one at the time effectively preventing multiple calls to the same target. 

Use the YAML below to create your Docker project. Change the ports by your own.

version: "3.9"

services:
  tcpqueue:
    container_name: tcpqueue

    # GEBRUIK EEN BESTAANDE IMAGE
    image: 192.168.1.201:5000/tcpqueue:latest   # pas aan als jouw tag anders is
    restart: unless-stopped

   # EXPOSE = alleen intern zichtbaar voor andere containers
    expose:
      - "15000"
      - "15240"
      - "15241"
      - "15242"
      - "15243"

    # Ports = publiceren naar jouw Windows host
    ports:
      - "15000:15000"
      - "15240:15240"
      - "15241:15241"
      - "15242:15242"
      - "15243:15243" 
      
    # run als root zodat je config-map leesbaar is
    user: "0:0"

    environment:
      CONFIG_PATH: "/tcpqueue/config"

    volumes:
      - /volume1/containers/tcpqueue:/tcpqueue

    